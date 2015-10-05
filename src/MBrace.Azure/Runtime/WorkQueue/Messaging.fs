﻿namespace MBrace.Azure.Runtime

open System
open System.IO
open System.Threading.Tasks

open Microsoft.ServiceBus.Messaging

open MBrace.Core.Internals
open MBrace.Runtime
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.Retry

open MBrace.Azure.Runtime
open MBrace.Azure.Runtime.Utilities

type internal MessagingClient =
    /// Generic work item lease token dequeue method
    static member TryDequeue (config : ClusterId, logger : ISystemLogger, localWorkerId : IWorkerId, dequeueF : unit -> Task<BrokeredMessage>) : Async<ICloudWorkItemLeaseToken option> = async { 
        let! (message : BrokeredMessage) = dequeueF()
        if message = null then 
            return None
        else 
            let jobInfo = WorkItemLeaseTokenInfo.FromReceivedMessage message
            logger.Logf LogLevel.Debug "%O : dequeued, delivery count = %d" jobInfo jobInfo.DeliveryCount 

            logger.Logf LogLevel.Debug "%O : starting lock renew loop" jobInfo
            let monitor = WorkItemLeaseMonitor.Start(config, message, jobInfo, logger)

            logger.Logf LogLevel.Debug "%O : changing status to %A" jobInfo WorkItemStatus.Dequeued
            let newRecord = new WorkItemRecord(jobInfo.ProcessId, fromGuid jobInfo.WorkItemId)
            newRecord.ETag <- "*"
            newRecord.Completed <- nullable false
            newRecord.DequeueTime <- nullable jobInfo.DequeueTime
            newRecord.Status <- nullable(int WorkItemStatus.Dequeued)
            newRecord.CurrentWorker <- localWorkerId.Id
            newRecord.DeliveryCount <- nullable jobInfo.DeliveryCount
            newRecord.FaultInfo <- nullable(int FaultInfo.NoFault)

            logger.Logf LogLevel.Debug "%O : fetching fault info" jobInfo
            let! faultInfo = async {
                let faultCount = jobInfo.DeliveryCount - 1

                if faultCount = 0 then
                    match jobInfo.TargetWorker with
                    | None -> return NoFault
                    | Some target when target = localWorkerId.Id -> return NoFault
                    | Some target ->
                        newRecord.FaultInfo <- nullable(int FaultInfo.IsTargetedWorkItemOfDeadWorker)
                        return IsTargetedWorkItemOfDeadWorker(faultCount, new WorkerId(target))
                else
                    let! oldRecord = Table.read<WorkItemRecord> config.StorageAccount config.RuntimeTable jobInfo.ProcessId (fromGuid jobInfo.WorkItemId)
                    // two cases:
                    match enum<FaultInfo> oldRecord.FaultInfo.Value with
                    // either worker declared workItem faulted
                    | FaultInfo.FaultDeclaredByWorker ->
                        let lastExc =
                            if oldRecord.LastException = null then Unchecked.defaultof<_>
                            else ProcessConfiguration.Serializer.UnPickle<ExceptionDispatchInfo>(oldRecord.LastException)
                        let lastWorker = new WorkerId(oldRecord.CurrentWorker)
                        return FaultDeclaredByWorker(faultCount, lastExc, lastWorker)
                    // or worker died
                    | _ ->
                        match jobInfo.TargetWorker with
                        | None ->
                            return WorkerDeathWhileProcessingWorkItem(faultCount, new WorkerId(oldRecord.CurrentWorker))
                        | Some target when target = localWorkerId.Id ->
                            newRecord.FaultInfo <- nullable(int FaultInfo.WorkerDeathWhileProcessingWorkItem)
                            return WorkerDeathWhileProcessingWorkItem(faultCount, new WorkerId(oldRecord.CurrentWorker))
                        | Some target ->
                            newRecord.FaultInfo <- nullable(int FaultInfo.IsTargetedWorkItemOfDeadWorker)
                            return IsTargetedWorkItemOfDeadWorker(faultCount, new WorkerId(target))
            }

            logger.Logf LogLevel.Debug "%O : extracted fault info %A" jobInfo faultInfo
            let! _record = Table.merge config.StorageAccount config.RuntimeTable newRecord
            logger.Logf LogLevel.Debug "%O : changed status successfully" jobInfo
            let! leaseToken = WorkItemLeaseToken.Create(config, jobInfo, monitor, faultInfo)
            return Some (leaseToken :> ICloudWorkItemLeaseToken)
    }

    /// Generic work item enqueue method
    static member Enqueue (config : ClusterId, logger : ISystemLogger, workItem : CloudWorkItem, allowNewSifts : bool, sendF : BrokeredMessage -> Task) = async { 
        // Step 1: initial record entry creation
        let record = WorkItemRecord.FromCloudWorkItem(workItem)
        do! Table.insert config.StorageAccount config.RuntimeTable record
        logger.Logf LogLevel.Debug "workItem:%O : enqueue" workItem.Id

        // Step 2: Persist work item payload to blob store
        let blobUri = sprintf "workItem/%s/%s" workItem.Process.Id (fromGuid workItem.Id)
        do! BlobPersist.PersistClosure<MessagePayload>(config, Single workItem, blobUri, allowNewSifts)
        let! size = BlobPersist.GetPersistedClosureSize(config, blobUri)

        // Step 3: update record entry
        let newRecord = record.CloneDefault()
        newRecord.Status <- nullable(int WorkItemStatus.Enqueued)
        newRecord.EnqueueTime <- nullable record.Timestamp
        newRecord.Size <- nullable size
        newRecord.FaultInfo <- nullable(int FaultInfo.NoFault)
        newRecord.ETag <- "*"
        let! _record = Table.merge config.StorageAccount config.RuntimeTable newRecord

        // Step 4: send work item message to service bus queue
        let msg = new BrokeredMessage(blobUri)
        msg.Properties.[ServiceBusSettings.WorkItemIdProperty] <- workItem.Id
        msg.Properties.[ServiceBusSettings.ParentTaskIdProperty] <- workItem.Process.Id
        workItem.TargetWorker |> Option.iter (fun t -> msg.Properties.[ServiceBusSettings.AffinityProperty] <- t.Id)
        do! sendF msg

        logger.Logf LogLevel.Debug "workItem:%O : enqueue completed, size %s" workItem.Id (getHumanReadableByteSize size)
    }

    /// Generic work item batch enqueue method
    static member EnqueueBatch(config : ClusterId, logger : ISystemLogger, jobs : CloudWorkItem [], sendF : BrokeredMessage seq -> Task) = async { 
        if jobs.Length = 0 then return () else // silent discard if empty
        // Step 1: initial work item record population
        let records = jobs |> Seq.map WorkItemRecord.FromCloudWorkItem
        do! Table.insertBatch config.StorageAccount config.RuntimeTable records

        // Step 2: persist payload to blob store
        let headJob = jobs.[0]
        let blobUri = sprintf "workItem/%s/batch/%s" headJob.Process.Id (fromGuid headJob.Id)
        do! BlobPersist.PersistClosure<MessagePayload>(config, Batch jobs, blobUri, allowNewSifts = false)
        let! size = BlobPersist.GetPersistedClosureSize(config, blobUri)

        // Step 3: update runtime records
        let now = DateTimeOffset.Now
        let newRecords = 
            records |> Seq.map (fun r -> 
                let newRec = r.CloneDefault()
                newRec.ETag <- "*"
                newRec.Status <- nullable(int WorkItemStatus.Enqueued)
                newRec.EnqueueTime <- nullable now
                newRec.FaultInfo <- nullable(int FaultInfo.NoFault)
                newRec.Size <- nullable(size)
                newRec)

        do! Table.mergeBatch config.StorageAccount config.RuntimeTable newRecords

        // Step 4: create work messages and post to service bus queue
        let mkWorkItemMessage (i : int) (workItem : CloudWorkItem) =
            let msg = new BrokeredMessage(blobUri)
            msg.Properties.[ServiceBusSettings.WorkItemIdProperty] <- workItem.Id
            msg.Properties.[ServiceBusSettings.ParentTaskIdProperty] <- workItem.Process.Id
            msg.Properties.[ServiceBusSettings.BatchIndexProperty] <- i
            workItem.TargetWorker |> Option.iter (fun t -> msg.Properties.[ServiceBusSettings.AffinityProperty] <- t.Id)
            msg

        let messages = jobs |> Array.mapi mkWorkItemMessage
        do! sendF messages
        logger.Logf LogLevel.Info "Enqueued batched jobs of %d items for task %s, total size %s." jobs.Length headJob.Process.Id (getHumanReadableByteSize size)
    }
    

/// Topic subscription client
[<Sealed; AutoSerializable(false)>]
type internal Subscription (config : ClusterId, targetWorkerId : IWorkerId, logger : ISystemLogger) = 
    do 
        let nsClient = config.ServiceBusAccount.NamespaceManager
        let topic = config.RuntimeTopic
        let affinity = targetWorkerId.Id
        if not <| nsClient.SubscriptionExists(topic, affinity) then 
            logger.Logf LogLevel.Info "Creating new subscription for %A" affinity
            let sd = new SubscriptionDescription(topic, affinity)
            sd.DefaultMessageTimeToLive <- ServiceBusSettings.MaxTTL
            sd.LockDuration <- ServiceBusSettings.MaxLockDuration
            sd.AutoDeleteOnIdle <- ServiceBusSettings.SubscriptionAutoDeleteInterval
            let filter = new SqlFilter(sprintf "%s = '%s'" ServiceBusSettings.AffinityProperty affinity)
            let _description = 
                retry (RetryPolicy.ExponentialDelay(3, 1.<sec>)) 
                      (fun () -> nsClient.CreateSubscription(sd, filter))
            ()
            

    let subscription = config.ServiceBusAccount.CreateSubscriptionClient(config.RuntimeTopic, targetWorkerId.Id)

    member this.TargetWorkerId = targetWorkerId

    member this.GetMessageCountAsync() = async {
        let! (descr : SubscriptionDescription) = config.ServiceBusAccount.NamespaceManager.GetSubscriptionAsync(config.RuntimeTopic, targetWorkerId.Id)
        return descr.MessageCount
    }

    member this.TryDequeue(currentWorker : IWorkerId) : Async<ICloudWorkItemLeaseToken option> = 
        MessagingClient.TryDequeue(config, logger, currentWorker, fun () -> subscription.ReceiveAsync(ServiceBusSettings.ServerWaitTime))

    member this.DequeueAllMessagesBatch() = async { 
        let! mc = this.GetMessageCountAsync()
        if mc < 1L then return [||]
        else
            let! messages = subscription.ReceiveBatchAsync(int mc) 
            return Seq.toArray messages
    }

/// Topic client implementation
[<Sealed; AutoSerializable(false)>]
type internal Topic (config : ClusterId, logger : ISystemLogger) = 
    let topic = config.ServiceBusAccount.CreateTopicClient(config.RuntimeTopic)

    member this.GetMessageCountAsync() = async {
        let! (td : TopicDescription) = config.ServiceBusAccount.NamespaceManager.GetTopicAsync(config.RuntimeTopic)
        return td.MessageCountDetails.ActiveMessageCount
    }

    member this.GetSubscription(subscriptionId : IWorkerId) : Subscription = new Subscription(config, subscriptionId, logger)
    
    member this.EnqueueBatch(jobs : CloudWorkItem []) : Async<unit> = 
        MessagingClient.EnqueueBatch(config, logger, jobs, topic.SendBatchAsync)
    
    member this.Enqueue(workItem : CloudWorkItem, allowNewSifts : bool) = 
        MessagingClient.Enqueue(config, logger, workItem, allowNewSifts, topic.SendAsync)

    static member Create(config, logger : ISystemLogger) = async { 
        let! exists = config.ServiceBusAccount.NamespaceManager.TopicExistsAsync(config.RuntimeTopic)
        if not exists then 
            logger.Logf LogLevel.Info "Creating new topic %A" config.RuntimeTopic
            let metadata = Metadata.Create config
            let qd = new TopicDescription(config.RuntimeTopic)
            qd.EnableBatchedOperations <- true
            qd.EnablePartitioning <- true
            qd.DefaultMessageTimeToLive <- ServiceBusSettings.MaxTTL
            qd.UserMetadata <- Metadata.ToJson metadata
            do! config.ServiceBusAccount.NamespaceManager.CreateTopicAsync(qd)
        else
            logger.Logf  LogLevel.Info "Topic %A exists." config.RuntimeTopic
        return new Topic(config, logger)
    }

/// Queue client implementation
[<Sealed; AutoSerializable(false)>]
type internal Queue (config : ClusterId, logger : ISystemLogger) = 
    let queue = config.ServiceBusAccount.CreateQueueClient(config.RuntimeQueue, ReceiveMode.PeekLock)

    member this.GetMessageCountAsync() = async {
        let! (qd : QueueDescription) = config.ServiceBusAccount.NamespaceManager.GetQueueAsync(config.RuntimeQueue)
        return qd.MessageCount
    }

    member this.EnqueueBatch(jobs : CloudWorkItem []) = 
        MessagingClient.EnqueueBatch(config, logger, jobs, queue.SendBatchAsync)
    
    member this.Enqueue(workItem : CloudWorkItem, allowNewSifts : bool) = 
        MessagingClient.Enqueue(config, logger, workItem, allowNewSifts, queue.SendAsync)
    
    member this.TryDequeue(workerId : IWorkerId) : Async<ICloudWorkItemLeaseToken option> = 
        MessagingClient.TryDequeue(config, logger, workerId, fun () -> queue.ReceiveAsync(ServiceBusSettings.ServerWaitTime))

    member this.EnqueueMessagesBatch(messages : seq<BrokeredMessage>) = async { return! queue.SendBatchAsync messages }
        
    static member Create(config : ClusterId, logger : ISystemLogger) = async { 
        let ns = config.ServiceBusAccount.NamespaceManager
        let! exists = ns.QueueExistsAsync(config.RuntimeQueue)
        if not exists then 
            logger.Logf LogLevel.Info "Creating new queue %A" config.RuntimeQueue
            let metadata = Metadata.Create config
            let qd = new QueueDescription(config.RuntimeQueue)
            qd.EnableBatchedOperations <- true
            qd.EnablePartitioning <- true
            qd.DefaultMessageTimeToLive <- ServiceBusSettings.MaxTTL 
            qd.MaxDeliveryCount <- ServiceBusSettings.MaxDeliveryCount
            qd.LockDuration <- ServiceBusSettings.MaxLockDuration
            qd.UserMetadata <- Metadata.ToJson metadata
            do! ns.CreateQueueAsync(qd)
        else
            logger.Logf LogLevel.Info "Queue %A exists." config.RuntimeQueue
        return new Queue(config, logger)
    }
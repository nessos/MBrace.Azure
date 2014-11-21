using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.Storage;
using Nessos.MBrace.Azure.Runtime;
using Nessos.MBrace.Azure.Runtime.Common;

namespace Nessos.MBrace.Azure.CloudService.WorkerRole
{
    public class WorkerRole : RoleEntryPoint
    {
        private Service _svc;

        public override void Run()
        {
            _svc.Start();
        }

        public override bool OnStart()
        {
            // Set the maximum number of concurrent connections
            ServicePointManager.DefaultConnectionLimit = 12;

            bool result = base.OnStart();

            var config = new Configuration(storageConnectionString : "", 
                                           serviceBusConnectionString : "",
                                           defaultContainer : Configuration.Default.DefaultContainer,
                                           defaultTable : Configuration.Default.DefaultTable,
                                           defaultQueue : Configuration.Default.DefaultQueue,
                                           defaultLogTable : Configuration.Default.DefaultLogTable);

            _svc = new Service(config, maxTasks : 10);
            var logger = new StorageLogger(config.DefaultLogTable, "worker", _svc.Id);
            logger.Attach(new CustomLogger(s => Trace.WriteLine(s)));
            _svc.Logger = logger;
            return result;
        }

        public override void OnStop()
        {
            base.OnStop();
        }
    }
}

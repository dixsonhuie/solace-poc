﻿using GigaSpaces.Core;
using CustomExternalDataSource.ExternalDataSource;
using GigaSpaces.Core.Persistency;

using SolaceSystems.Solclient.Messaging;
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using GigaSpaces.Core.Metadata;
using System.Text;

namespace Piper.Processor
{
    public class SolaceExternalDataSource : NHibernateSpaceDataSource
    {

        /// <summary>
        /// MaxAttempts is the number of times that the external data source will attempt to persist data.
        /// </summary>
        int MaxAttempts { get; set; }

        string SpaceName { get; set; }

        // used for publishing to Solace
        string Host { get; set; }
        string UserName { get; set; }

        string Password { get; set; }
        string VpnName { get; set; }

        int ConnectRetries { get; set; }

        SolaceSystems.Solclient.Messaging.ISession session;

        IQueue queue;

        // end used for publishing to Solace

        ISpaceProxy? spaceProxy;

        string AssemblyFilePath { get; set; }

        Assembly assembly;

        const bool debuglevel = false;

        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();


        class MsgInfo
        {
            public bool Acked { get; set; }
            public bool Accepted { get; set; }
            public readonly IMessage Message;

            public MsgInfo(IMessage message)
            {
                Acked = false;
                Accepted = false;
                Message = message;
            }
        }
        public SolaceExternalDataSource()
        {
        }

        public override void Init(Dictionary<string, string> properties)
        {
            base.Init(properties);

            SpaceName = GetProperty("SpaceName");

            MaxAttempts = GetIntProperty("MaxAttempts", 3);

            Host = GetProperty("Solace.Host");

            UserName = GetProperty("Solace.UserName");

            Password = GetProperty("Solace.Password");

            VpnName = GetProperty("Solace.VpnName");

            if (VpnName == null) { VpnName = ""; }

            ConnectRetries = GetIntProperty("Solace.ConnectRetries", -1);

            AssemblyFilePath = GetFileProperty("AssemblyFileName");

            assembly = Assembly.LoadFrom(AssemblyFilePath);

            Logger.Info("SpaceName is: " + SpaceName);
            Logger.Info("MaxAttempts is: " + MaxAttempts);
            Logger.Info("Host is: " + Host);
            Logger.Info("UserName is: " + UserName);
            Logger.Info("VpnName is: " + VpnName);
            Logger.Info("ConnectRetries is: " + ConnectRetries);
            Logger.Info("AssemblyFilePath is: " + AssemblyFilePath);
            Logger.Info("Assemlby.FullName is: " + assembly.FullName);


            solaceInit();
        }
        
        private void solaceInit()
        {
            // Initialize Solace Systems Messaging API with logging to console at Warning level
            ContextFactoryProperties cfp = new ContextFactoryProperties()
            {
                SolClientLogLevel = SolLogLevel.Warning
            };
            cfp.LogToConsoleError();
            ContextFactory.Instance.Init(cfp);

            try
            {
                IContext context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);
                Run(context, Host);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception thrown: {0}", ex.Message);
            }

        }

        void Run(IContext context, string host)
        {
            if (context == null)
            {
                throw new ArgumentException("Solace Systems API context Router must be not null.", "context");
            }
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Solace Messaging Router host name must be non-empty.", "host");
            }
            if (string.IsNullOrWhiteSpace(VpnName))
            {
                throw new InvalidOperationException("VPN name must be non-empty.");
            }
            if (string.IsNullOrWhiteSpace(UserName))
            {
                throw new InvalidOperationException("Client username must be non-empty.");
            }

            // Create session properties
            // When solace reconnect retries is -1, forever try to connect
            SessionProperties sessionProps = new SessionProperties()
            {
                Host = host,
                VPNName = this.VpnName,
                UserName = this.UserName,
                Password = this.Password,
                ConnectRetries = this.ConnectRetries,
                ReconnectRetries = this.ConnectRetries
            };

            // Connect to the Solace messaging router
            Console.WriteLine("Connecting as {0}@{1} on {2}...", UserName, VpnName, host);
            // NOTICE HandleSessionEvent as session event handler
            session = context.CreateSession(sessionProps, null, HandleSessionEvent);
            ReturnCode returnCode = session.Connect();
            if (returnCode == ReturnCode.SOLCLIENT_OK)
            {
                Console.WriteLine("Session successfully connected.");
            }
            else
            {
                Console.WriteLine("Error connecting, return code: {0}", returnCode);
            }
        }
        void HandleSessionEvent(object sender, SessionEventArgs args)
        {
            // Received a session event
            if (debuglevel)
            {
                Console.WriteLine("Received session event {0}.", args.ToString());
            }
            switch (args.Event)
            {
                // this is the confirmation
                case SessionEvent.Acknowledgement:
                    if (debuglevel)
                    {
                        Console.WriteLine("SessionEvent.Acknowledgement {0}.", args.ToString());
                    }
                    MsgInfo messageRecord = args.CorrelationKey as MsgInfo;
                    if (messageRecord != null)
                    {
                        messageRecord.Acked = true;
                        messageRecord.Accepted = args.Event == SessionEvent.Acknowledgement;
                    }
                    break;
                case SessionEvent.RejectedMessageError:
                    if (debuglevel)
                    {
                        Console.WriteLine("SessionEvent.RejectedMessageError : message record rejected " + args.ResponseCode);
                    }
                    MsgInfo _messageRecord = args.CorrelationKey as MsgInfo;
                    if (_messageRecord != null)
                    {
                        _messageRecord.Acked = false;
                        _messageRecord.Accepted = false;
                    }
                    break;
                default:
                    break;
            }
        }

        public override void ExecuteBulk(IList<BulkItem> bulk)
        {
            ExecuteBulk(bulk, 0);
        }
        protected virtual void ExecuteBulk(IList<BulkItem> bulk, int attempts)
        {
            JArray jsonArray = new JArray();
            try
            {
                foreach (BulkItem bulkItem in bulk)
                {
                    // ExecuteBulkItem(bulkItem, retries);
                    jsonArray.Add(createJsonResponse(bulkItem));
                }
            }
            catch (Exception e)
            {
                if (attempts >= MaxAttempts)
                    throw new Exception("Can't execute bulk store.", e);

                if (attempts == 1)
                {
                    Logger.Error("Retrying ... " + attempts, e);
                }
                ExecuteBulk(bulk, attempts + 1);
            }
            SendMessage(jsonArray);
        }

        protected virtual void ExecuteBulkItem(BulkItem bulkItem, int attempts)
        {
            JArray jsonArray = new JArray();
            try
            {
                jsonArray.Add(createJsonResponse(bulkItem));
            }
            catch (Exception e)
            {
                if (attempts >= MaxAttempts)
                    throw new Exception("Can't execute bulk store.", e);
                ExecuteBulkItem(bulkItem, attempts + 1);
            }
            SendMessage(jsonArray);
        }

        JObject createJsonResponse(BulkItem bulkItem)
        {
            setSpaceProxyIfNull();

            //call Once move to above

            object itemValue = bulkItem.Item;

            Logger.Info("AssemblyFilePath is: " + AssemblyFilePath);
            Logger.Info("assembly.FullName is: ", assembly.FullName);
            Logger.Info("bulkItem.Item.GetType() is: " + bulkItem.Item.GetType());
            Logger.Info("bulkItem.Item.GetType().FullName is: " + bulkItem.Item.GetType().FullName);

            Type type = assembly.GetType(bulkItem.Item.GetType().FullName);
            PropertyInfo[] propertyInfo = type.GetProperties();

            string[] propertyNames = new string[propertyInfo.Length];
            string[] propertyType = new string[propertyInfo.Length];
            object[] propertyValue = new object[propertyInfo.Length];
            JObject mainObject = new JObject();
            mainObject["op"] = bulkItem.Operation.ToString();
            mainObject["type"] = bulkItem.Item.GetType().Name;
            ISpaceTypeDescriptor spaceTypeDescriptor = spaceProxy.TypeManager.GetTypeDescriptor(bulkItem.Item.GetType());
            mainObject["spaceId"] = spaceTypeDescriptor.IdPropertyName;

            JArray payload = new JArray();

            for (int i = 0; i < propertyInfo.Length; i++)
            {
                JObject fieldDetails = new JObject();
                fieldDetails["columnName"] = propertyInfo[i].Name;
                object itemValTmp = itemValue.GetType().GetProperty(propertyInfo[i].Name).GetValue(itemValue);
                fieldDetails["value"] = new JValue(itemValTmp);
                Type typeTmp = propertyInfo[i].PropertyType;
                if (typeTmp.IsGenericType && typeTmp.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    fieldDetails["columnType"] = Nullable.GetUnderlyingType(typeTmp).Name;
                }
                else
                {
                    fieldDetails["columnType"] = typeTmp.Name;
                }

                propertyNames[i] = propertyInfo[i].Name;
                propertyValue[i] = itemValue.GetType().GetProperty(propertyInfo[i].Name).GetValue(itemValue, null);
                propertyType[i] = propertyInfo[i].PropertyType.Name;
                payload.Add(fieldDetails);
            }
            mainObject.Add("payload", payload);

            return mainObject;
        }

        void setSpaceProxyIfNull()
        {
            if (spaceProxy == null)
            {
                SpaceProxyFactory factory = new SpaceProxyFactory(SpaceName);
                //factory.LookupGroups = LookupGroup;
                try
                {
                    spaceProxy = factory.Create();
                    spaceProxy.Count(new object());
                    //assembly = Assembly.LoadFrom(AssemblyFilePath);
                    setSolaceSession();
                }
                catch (Exception e)
                {
                    Logger.Error(e.Message, e);
                    Console.WriteLine("Space verification failed. Please check & try again...");
                }
            }
        }

        void setSolaceSession()
        {
            // Provision the queue
            string queueName = SpaceName;
            Console.WriteLine("Attempting to provision the queue '{0}'...", queueName);
            queue = ContextFactory.Instance.CreateQueue(queueName);
            // Set queue permissions to "consume" and access-type to "exclusive"
            EndpointProperties endpointProps = new EndpointProperties()
            {
                Permission = EndpointProperties.EndpointPermission.Consume,
                AccessType = EndpointProperties.EndpointAccessType.Exclusive
            };
            // Provision it, and do not fail if it already exists
            session.Provision(queue, endpointProps,
                ProvisionFlag.IgnoreErrorIfEndpointAlreadyExists | ProvisionFlag.WaitForConfirm, null);
            Console.WriteLine("Queue '{0}' has been created and provisioned.", queueName);
        }

        private void SendMessage(JArray data)
        {

            // Create the queue
            //TODO should be once queue creation
            // using (IQueue queue = ContextFactory.Instance.CreateQueue(queueName))
            // {
            // Create the message
            using (IMessage message = ContextFactory.Instance.CreateMessage())
            {
                // Message's destination is the queue and the message is persistent
                message.Destination = queue;
                message.DeliveryMode = MessageDeliveryMode.Persistent;

                // Create the message content as a binary attachment
                message.BinaryAttachment = Encoding.ASCII.GetBytes(
                    Newtonsoft.Json.JsonConvert.SerializeObject(data));

                // Create a message correlation object
                MsgInfo msgInfo = new MsgInfo(message);
                message.CorrelationKey = msgInfo;

                // Send the message to the queue on the Solace messaging router
                Console.WriteLine("Sending message to queue {0}...", SpaceName);
                ReturnCode returnCode = session.Send(message);
                if (returnCode != ReturnCode.SOLCLIENT_OK)
                {
                    Console.WriteLine("Message sending failed, return code: {0}", returnCode);

                    // Throw exception for failed message
                    throw new Exception("Msg sending failed");
                }
                else
                {
                    if (debuglevel)
                    {
                        Console.WriteLine("Message sent successfully, return code: {0}", returnCode);
                    }
                }
            }
         }

        /*
        
        public override void ExecuteBulk(IList<BulkItem> bulk)
        {
            Console.WriteLine("Here in Solace");
            ExecuteBulk(bulk, 0);
        }
        protected virtual void ExecuteBulk(IList<BulkItem> bulk, int attempts)
        {
            try
            {
                foreach (BulkItem bulkItem in bulk)
                {
                    ExecuteBulkItem(bulkItem, attempts);
                }
            }
            catch (Exception e)
            {
                if (attempts >= MaxAttempts)
                    throw new Exception("Can't execute bulk store.", e);
                ExecuteBulk(bulk, attempts + 1);
            }
        }
        protected virtual void ExecuteBulkItem(BulkItem bulkItem, int attempts)
        {
            object entry = bulkItem.Item;


            switch (bulkItem.Operation)
            {
                case BulkOperation.Remove:
                    //session.Delete(session.Merge(entry));
                    Logger.Info("Remove called for: {0}", entry);

                    break;
                case BulkOperation.Write:
                    Console.WriteLine("Write called for: {0}", entry);
                    Logger.Info("Write called for: {0}", entry);
                    break;
                case BulkOperation.Update:
                    Logger.Info("Update called for: {0}", entry);
                    break;
                default:
                    break;
            }

        }
        */

    }

}

﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
#if NET45 || NET40
using System.Runtime.Remoting.Messaging;
#endif
using System.Security;
using System.Text;
using System.Threading.Tasks;
using StackifyLib;
using System.Diagnostics;
using StackifyLib.Internal.Logs;
using StackifyLib.Models;
using StackifyLib.Utils;
using NLog.Targets;
using NLog;

namespace NLog.Targets.Stackify
{
    [Target("StackifyTarget")]
    public class StackifyTarget : TargetWithLayout 
    {
        private bool _HasContextKeys = false;
        public string apiKey { get; set; }
        public string uri { get; set; }
        public string globalContextKeys { get; set; }
        public string mappedContextKeys { get; set; }
        public string callContextKeys { get; set; }
        public bool? logMethodNames { get; set; }
        public bool? logAllParams { get; set; }

        private List<string> _GlobalContextKeys = new List<string>();
        private List<string> _MappedContextKeys = new List<string>();
        private List<string> _CallContextKeys = new List<string>();

        private LogClient _logClient = null;

        protected override void CloseTarget()
        {
            try
            {
                StackifyLib.Utils.StackifyAPILogger.Log("NLog target closing");
                _logClient.Close();
                StackifyLib.Internal.Metrics.MetricClient.StopMetricsQueue("NLog CloseTarget");
            }
            catch (Exception ex)
            {
                StackifyLib.Utils.StackifyAPILogger.Log("NLog target closing error: " + ex.ToString());
            }
        }

        protected override void InitializeTarget()
        {
            StackifyLib.Utils.StackifyAPILogger.Log("NLog InitializeTarget");

            _logClient = new LogClient("StackifyLib.net-nlog", apiKey, uri);
            if (!String.IsNullOrEmpty(globalContextKeys))
            {
                _GlobalContextKeys = globalContextKeys.Split(',').Select(s => s.Trim()).ToList();
            }

            if (!String.IsNullOrEmpty(mappedContextKeys))
            {
                _MappedContextKeys = mappedContextKeys.Split(',').Select(s => s.Trim()).ToList();
            }

            if (!String.IsNullOrEmpty(callContextKeys))
            {
                _CallContextKeys = callContextKeys.Split(',').Select(s => s.Trim()).ToList();
            }


            _HasContextKeys = _GlobalContextKeys.Any() || _MappedContextKeys.Any() || _CallContextKeys.Any();
        }

        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                //make sure the buffer isn't overflowing
                //if it is skip since we can't do anything with the message
                if (StackifyLib.Logger.PrefixEnabled() || _logClient.CanQueue())
                {
                    var logMsg = Translate(logEvent);
                    if (logMsg != null)
                    {
                        _logClient.QueueMessage(logMsg);
                    }
                }
                else
                {
                    StackifyAPILogger.Log("Unable to send log because the queue is full");
                }
            }
            catch (Exception ex)
            {
                StackifyAPILogger.Log(ex.ToString());
            }

        }


        private Dictionary<string, object> GetDiagnosticContextProperties()
        {


            Dictionary<string, object> properties = new Dictionary<string, object>();


            string ndc = NLog.NestedDiagnosticsContext.TopMessage;

            if (!String.IsNullOrEmpty(ndc))
            {
                properties["ndc"] = ndc;
            }


            if (!_HasContextKeys)
            {
                return properties;
            }

            // GlobalDiagnosticsContext

            foreach (string gdcKey in _GlobalContextKeys)
            {
                if (NLog.GlobalDiagnosticsContext.Contains(gdcKey))
                {
                    string gdcValue = NLog.GlobalDiagnosticsContext.Get(gdcKey);

                    if (gdcValue != null)
                    {
                        properties.Add(gdcKey.ToLower(), gdcValue);
                    }
                }
            }
            // MappedDiagnosticsContext

            foreach (string mdcKey in _MappedContextKeys)
            {
                if (NLog.MappedDiagnosticsContext.Contains(mdcKey))
                {
                    string mdcValue = NLog.MappedDiagnosticsContext.Get(mdcKey);

                    if (mdcValue != null)
                    {
                        properties.Add(mdcKey.ToLower(), mdcValue);
                    }
                }
            }

#if NET45 || NET40

            foreach (string key in _CallContextKeys)
            {
                object value = CallContext.LogicalGetData(key);

                if (value != null)
                {
                    properties[key.ToLower()] = value;
                }
            }
#endif

            return properties;
        }

        internal LogMsg Translate(LogEventInfo loggingEvent)
        {

            if (loggingEvent == null)
                return null;

            //do not log our own messages. This is to prevent any sort of recursion that could happen since calling to send this will cause even more logging to happen
            if (loggingEvent.FormattedMessage != null && loggingEvent.FormattedMessage.IndexOf("StackifyLib:", StringComparison.OrdinalIgnoreCase) > -1)
                return null;

            StackifyLib.Models.LogMsg msg = new LogMsg();


            if (loggingEvent.Level != null)
            {
                msg.Level = loggingEvent.Level.Name;
            }

         

            if (loggingEvent.HasStackTrace && loggingEvent.UserStackFrame != null)
            {
                var frame = loggingEvent.UserStackFrame;

                MethodBase method = frame.GetMethod();
                if (method != (MethodBase) null && method.DeclaringType != (Type) null)
                {
                    if (method.DeclaringType != (Type) null)
                    {
                        msg.SrcMethod = method.DeclaringType.FullName + "." + method.Name;
                        msg.SrcLine = frame.GetFileLineNumber();
                    }
                }

            }


            //if it wasn't set above for some reason we will do it this way as a fallback
            if (string.IsNullOrEmpty(msg.SrcMethod))
            {
                msg.SrcMethod = loggingEvent.LoggerName;

                if ((logMethodNames ?? false))
                {
                    var frames = StackifyLib.Logger.GetCurrentStackTrace(loggingEvent.LoggerName, 1, true);

                    if (frames.Any())
                    {
                        var first = frames.First();

                        msg.SrcMethod = first.Method;
                        msg.SrcLine = first.LineNum;
                    }
                }
            }

            string formattedMessage;

            //Use the layout render to allow custom fields to be logged, but not if it is the default format as it logs a bunch fields we already log
            //really no reason to use a layout at all
            if (this.Layout != null && this.Layout.ToString() != "'${longdate}|${level:uppercase=true}|${logger}|${message}'") //do not use if it is the default
            {
                formattedMessage = this.Layout.Render(loggingEvent);
            }
            else
            {
                formattedMessage = loggingEvent.FormattedMessage;
            }

            msg.Msg = (formattedMessage ?? "").Trim();

            object debugObject = null;
            Dictionary<string, object> args = new Dictionary<string, object>();

            if ((loggingEvent.Parameters != null) && (loggingEvent.Parameters.Length > 0))
            {

                for (int i = 0; i < loggingEvent.Parameters.Length; i++)
                {
                    var item = loggingEvent.Parameters[i];

                    if (item == null)
                    {
                        continue;
                    }
                    else if (item is Exception)
                    {
                        if (loggingEvent.Exception == null)
                        {
                            loggingEvent.Exception = (Exception)item;
                        }
                    }
                    else if (item.ToString() == msg.Msg)
                    {
                        //ignore it.   
                    }
                    else if (logAllParams ?? true)
                    {
                        args["arg" + i] = loggingEvent.Parameters[i];
                        debugObject = item;
                    }
                    else
                    {
                        debugObject = item;
                    }
                }

                if ((logAllParams ?? true) && args != null && args.Count > 1)
                {
                    debugObject = args;
                }
            }


            StackifyError error = null;

            if (loggingEvent.Exception != null && loggingEvent.Exception is StackifyError)
            {
                error = (StackifyError) loggingEvent.Exception;
            }
            else if (loggingEvent.Exception != null)
            {
                error = StackifyError.New((Exception)loggingEvent.Exception);
            }

            var diags = GetDiagnosticContextProperties();
            if (diags != null && diags.ContainsKey("transid"))
            {
                msg.TransID = diags["transid"].ToString();
                diags.Remove("transid");
            }


     





            if (debugObject != null)
            {
                msg.data = StackifyLib.Utils.HelperFunctions.SerializeDebugData(debugObject, true, diags);
            }
            else
            {
                msg.data = StackifyLib.Utils.HelperFunctions.SerializeDebugData(null, false, diags);
            }
          

            if (msg.Msg != null && error != null)
            {
                msg.Msg += "\r\n" + error.ToString();
            }
            else if (msg.Msg == null && error != null)
            {
                msg.Msg = error.ToString();
            }

            if (error == null && (loggingEvent.Level == LogLevel.Error || loggingEvent.Level == LogLevel.Fatal))
            {
                StringException stringException = new StringException(msg.Msg);

                stringException.TraceFrames = StackifyLib.Logger.GetCurrentStackTrace(loggingEvent.LoggerName);

                if (!loggingEvent.HasStackTrace || loggingEvent.UserStackFrame == null)
                {
                    if (stringException.TraceFrames.Any())
                    {
                        var first = stringException.TraceFrames.First();

                        msg.SrcMethod = first.Method;
                        msg.SrcLine = first.LineNum;
                    }
                }

                //Make error out of log message
                error = StackifyError.New(stringException);
            }

            if (error != null && !StackifyError.IgnoreError(error) && _logClient.ErrorShouldBeSent(error))
            {
                error.SetAdditionalMessage(formattedMessage);
                msg.Ex = error;
            }
            else if (error != null && msg.Msg != null)
            {
                msg.Msg += " #errorgoverned";
            }


            return msg;
        }


    
    }
}

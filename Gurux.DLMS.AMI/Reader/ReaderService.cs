﻿//
// --------------------------------------------------------------------------
//  Gurux Ltd
//
//
//
// Filename:        $HeadURL$
//
// Version:         $Revision$,
//                  $Date$
//                  $Author$
//
// Copyright (c) Gurux Ltd
//
//---------------------------------------------------------------------------
//
//  DESCRIPTION
//
// This file is a part of Gurux Device Framework.
//
// Gurux Device Framework is Open Source software; you can redistribute it
// and/or modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; version 2 of the License.
// Gurux Device Framework is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// This code is licensed under the GNU General Public License v2.
// Full text may be retrieved at http://www.gnu.org/licenses/gpl-2.0.txt
//---------------------------------------------------------------------------
using Gurux.Common;
using Gurux.DLMS.Enums;
using Gurux.DLMS.Objects;
using Gurux.DLMS.Secure;
using Gurux.Net;
using Gurux.DLMS.AMI.Messages.DB;
using Gurux.DLMS.AMI.Messages.Enums;
using Gurux.DLMS.AMI.Messages.Rest;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using Gurux.Serial;
using Microsoft.Extensions.Options;
using System.Reflection;
using Gurux.Terminal;
using Gurux.DLMS.AMI.Internal;

namespace Gurux.DLMS.AMI.Reader
{
    internal class ReaderService : IHostedService, IDisposable
    {
        AutoResetEvent closing = new AutoResetEvent(false);
        private DateTime lastUpdated = DateTime.MinValue;
        private readonly ILogger _logger;
        private readonly System.Net.Http.HttpClient client = new System.Net.Http.HttpClient();
        private readonly Guid _guid;
        private readonly int _threads;
        private readonly string _name;
        private readonly int _waitTime;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="logger"></param>
        public ReaderService(ILogger<TimedHostedService> logger, IOptions<ReaderOptions> optionsAccessor)
        {
            _logger = logger;
            _guid = Guid.Parse(optionsAccessor.Value.Id);
            _threads = optionsAccessor.Value.Threads;
            _name = optionsAccessor.Value.Name;
            _waitTime = optionsAccessor.Value.TaskWaitTime;
        }

        public System.Threading.Tasks.Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Reader Service is starting.");
            GXReaderInfo r = new GXReaderInfo();
            r.Guid = _guid;
            r.Name = _name;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(typeof(ReaderService).Assembly.Location);
            r.Version = info.FileVersion;
            //Don't wait reply. It might that DB server is not up yet.
            client.PostAsJsonAsync(Startup.ServerAddress + "/api/reader/AddReader", new AddReader() { Reader = r });
            for (int pos = 0; pos != _threads; ++pos)
            {
                Thread t3 = new Thread(() => DoWork());
                t3.Start();
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private async void DoWork()
        {
            _logger.LogInformation("Reader Service is working.");
            GetNextTaskResponse ret = null;
            GXDLMSObjectCollection objects = new GXDLMSObjectCollection();
            System.Net.Http.HttpResponseMessage response;
            while (!closing.WaitOne(1))
            {
                try
                {
                    using (response = await client.PostAsJsonAsync(Startup.ServerAddress + "/api/task/GetNextTask", new GetNextTask()))
                    {
                        Helpers.CheckStatus(response);
                        ret = await response.Content.ReadAsAsync<GetNextTaskResponse>();
                    }
                    if (ret.Tasks != null)
                    {
                        int pos = 0;
                        GXDevice dev;
                        GXDLMSSecureClient cl;
                        using (response = await client.PostAsJsonAsync(Startup.ServerAddress + "/api/device/ListDevices", new ListDevices() { Ids = new[] { ret.Tasks[0].Object.DeviceId } }))
                        {
                            Helpers.CheckStatus(response);
                            ListDevicesResponse r = await response.Content.ReadAsAsync<ListDevicesResponse>();
                            if (r.Devices == null || r.Devices.Length == 0)
                            {
                                continue;
                            }
                            dev = r.Devices[0];
                        }
                        IGXMedia media;
                        if (string.Compare(dev.MediaType, typeof(GXNet).FullName, true) == 0)
                        {
                            media = new GXNet();
                        }
                        else if (string.Compare(dev.MediaType, typeof(GXSerial).FullName, true) == 0)
                        {
                            media = new GXSerial();
                        }
                        else if (string.Compare(dev.MediaType, typeof(GXTerminal).FullName, true) == 0)
                        {
                            media = new GXTerminal();
                        }
                        else
                        {
                            Type type = Type.GetType(dev.MediaType);
                            if (type == null)
                            {
                                string ns = "";
                                pos = dev.MediaType.LastIndexOf('.');
                                if (pos != -1)
                                {
                                    ns = dev.MediaType.Substring(0, pos);
                                }
                                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                                {
                                    if (assembly.GetName().Name == ns)
                                    {
                                        if (assembly.GetType(dev.MediaType, false, true) != null)
                                        {
                                            type = assembly.GetType(dev.MediaType);
                                        }
                                    }
                                }
                            }
                            if (type == null)
                            {
                                throw new Exception("Invalid media type: " + dev.MediaType);
                            }
                            media = (IGXMedia)Activator.CreateInstance(type);
                        }
                        if (media == null)
                        {
                            throw new Exception("Unknown media type '" + dev.MediaType + "'.");
                        }
                        media.Settings = dev.MediaSettings;
                        GXDLMSReader reader;
                        //Read frame counter from the meter.
                        if (dev.Security != 0)
                        {
                            cl = new GXDLMSSecureClient(dev.UseLogicalNameReferencing, 16, dev.PhysicalAddress,
                                Authentication.None, null, (InterfaceType)dev.InterfaceType);
                            reader = new GXDLMSReader(cl, media, TraceLevel.Verbose, _logger);
                            media.Open();
                            reader.InitializeConnection();
                            //Read Innovation counter.
                            GXDLMSData d = new GXDLMSData(dev.FrameCounter);
                            reader.Read(d, 2);
                            dev.InvocationCounter = 1 + Convert.ToUInt32(d.Value);
                            reader.Disconnect();
                            media.Close();
                        }
                        cl = new GXDLMSSecureClient(dev.UseLogicalNameReferencing, dev.ClientAddress, dev.PhysicalAddress,
                            (Authentication)dev.Authentication, dev.Password, (InterfaceType)dev.InterfaceType);
                        if (dev.HexPassword != null && dev.HexPassword.Length != 0)
                        {
                            cl.Password = dev.HexPassword;
                        }
                        cl.UtcTimeZone = dev.UtcTimeZone;
                        cl.Standard = (Standard)dev.Standard;
                        cl.Ciphering.SystemTitle = GXCommon.HexToBytes(dev.ClientSystemTitle);
                        if (cl.Ciphering.SystemTitle != null && cl.Ciphering.SystemTitle.Length == 0)
                        {
                            cl.Ciphering.SystemTitle = null;
                        }
                        cl.Ciphering.BlockCipherKey = GXCommon.HexToBytes(dev.BlockCipherKey);
                        if (cl.Ciphering.BlockCipherKey != null && cl.Ciphering.BlockCipherKey.Length == 0)
                        {
                            cl.Ciphering.BlockCipherKey = null;
                        }
                        cl.Ciphering.AuthenticationKey = GXCommon.HexToBytes(dev.AuthenticationKey);
                        if (cl.Ciphering.AuthenticationKey != null && cl.Ciphering.AuthenticationKey.Length == 0)
                        {
                            cl.Ciphering.AuthenticationKey = null;
                        }
                        cl.ServerSystemTitle = GXCommon.HexToBytes(dev.DeviceSystemTitle);
                        if (cl.ServerSystemTitle != null && cl.ServerSystemTitle.Length == 0)
                        {
                            cl.ServerSystemTitle = null;
                        }
                        cl.Ciphering.InvocationCounter = dev.InvocationCounter;
                        cl.Ciphering.Security = (Security)dev.Security;
                        reader = new GXDLMSReader(cl, media, TraceLevel.Verbose, _logger);
                        media.Open();
                        reader.InitializeConnection();
                        pos = 0;
                        int count = ret.Tasks.Length;
                        foreach (GXTask task in ret.Tasks)
                        {
                            ++pos;
                            try
                            {
                                GXDLMSObject obj = GXDLMSClient.CreateObject((ObjectType)task.Object.ObjectType);
                                obj.LogicalName = task.Object.LogicalName;
                                obj.ShortName = task.Object.ShortName;
                                if (task.TaskType == TaskType.Write)
                                {
                                    if (obj.LogicalName == "0.0.1.1.0.255" && task.Index == 2)
                                    {
                                        cl.UpdateValue(obj, task.Index, GXDateTime.ToUnixTime(DateTime.UtcNow));
                                    }
                                    else
                                    {
                                        cl.UpdateValue(obj, task.Index, GXDLMSTranslator.XmlToValue(task.Data));
                                    }
                                    reader.Write(obj, task.Index);
                                }
                                else if (task.TaskType == TaskType.Action)
                                {
                                    reader.Method(obj, task.Index, GXDLMSTranslator.XmlToValue(task.Data), DataType.None);
                                }
                                else if (task.TaskType == TaskType.Read)
                                {
                                    //Reading the meter.
                                    Reader.Read(_logger, client, reader, task, media, obj);
                                }
                            }
                            catch (Exception ex)
                            {
                                task.Result = ex.Message;
                                AddError error = new AddError();
                                error.Error = new GXError()
                                {
                                    DeviceId = dev.Id,
                                    Error = "Failed to " + task.TaskType + " " + task.Object.LogicalName + ":" + task.Index + ". " + ex.Message
                                };
                                using (response = await client.PostAsJsonAsync(Startup.ServerAddress + "/api/error/AddError", error))
                                {
                                    Helpers.CheckStatus(response);
                                    await response.Content.ReadAsAsync<AddErrorResponse>();
                                }
                            }
                            task.End = DateTime.Now;
                            //Close connection after last task is executed.
                            //This must done because there might be new task to execute.
                            if (count == pos)
                            {
                                try
                                {
                                    reader.Close();
                                }
                                catch (Exception ex)
                                {
                                    task.Result = ex.Message;
                                    AddError error = new AddError();
                                    error.Error = new GXError()
                                    {
                                        DeviceId = dev.Id,
                                        Error = "Failed to close the connection. " + ex.Message
                                    };
                                    using (response = await client.PostAsJsonAsync(Startup.ServerAddress + "/api/error/AddError", error))
                                    {
                                        Helpers.CheckStatus(response);
                                        await response.Content.ReadAsAsync<AddErrorResponse>();
                                    }
                                }
                            }
                            //Update execution time.
                            response = client.PostAsJsonAsync(Startup.ServerAddress + "/api/task/TaskReady", new TaskReady() { Tasks = new GXTask[] { task } }).Result;
                            Helpers.CheckStatus(response);
                        }
                    }
                    else
                    {
                        try
                        {
                            using (response = await client.PostAsJsonAsync(Startup.ServerAddress + "/api/task/WaitChange", new WaitChange() { Change = TargetType.Tasks, Time = lastUpdated, WaitTime = _waitTime }))
                            {
                                Helpers.CheckStatus(response);
                                {
                                    WaitChangeResponse r = await response.Content.ReadAsAsync<WaitChangeResponse>();
                                    if (r.Time > lastUpdated)
                                    {
                                        lastUpdated = r.Time;
                                    }
                                }
                            }
                        }
                        catch (Exception)
                        {
                            Thread.Sleep(10000);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ret == null)
                    {
                        _logger.LogInformation("Failed to connect to the DB server.");
                    }
                    else
                    {
                        AddError error = new AddError();
                        error.Error = new GXError()
                        {
                            DeviceId = ret.Tasks[0].Object.DeviceId,
                            Error = "Failed to " + ret.Tasks[0].TaskType + " " + ret.Tasks[0].Object.LogicalName + ":" + ret.Tasks[0].Index + ". " + ex.Message
                        };
                        using (response = await client.PostAsJsonAsync(Startup.ServerAddress + "/api/error/AddError", error))
                        {
                            if (ret.Tasks != null)
                            {
                                DateTime now = DateTime.Now;
                                foreach (GXTask it in ret.Tasks)
                                {
                                    it.Result = ex.Message;
                                    it.End = now;
                                }
                                response = await client.PostAsJsonAsync(Startup.ServerAddress + "/api/task/TaskReady", new TaskReady() { Tasks = ret.Tasks });
                            }
                        }
                    }
                    _logger.LogInformation(ex.Message);
                    Console.WriteLine(ex.Message);
                    if (!(ex is GXDLMSException))
                    {
                        Console.WriteLine("+++++++++++++++++++++++++");
                        Console.WriteLine(ex);
                    }
                }
            }
        }

        private void Cl_OnPdu(object sender, byte[] data)
        {
            string str = GXCommon.ToHex(data);
            Console.WriteLine(str);
            _logger.LogInformation(str);
            GXDLMSTranslator t = new GXDLMSTranslator(TranslatorOutputType.SimpleXml);
            str = t.PduToXml(data);
            Console.WriteLine(str);
            _logger.LogInformation(str);
        }

        public System.Threading.Tasks.Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Reader Service is stopping.");
            closing.Set();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public void Dispose()
        {

        }
    }
}

﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WatchDog.src.Helpers;
using WatchDog.src.Models;

namespace WatchDog.src
{
    public class WatchDog
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly RecyclableMemoryStreamManager _recyclableMemoryStreamManager;

        public WatchDog(RequestDelegate next, ILoggerFactory loggerFactory)
        {
            _next = next;
            _logger = loggerFactory.CreateLogger<WatchDog>();
            _recyclableMemoryStreamManager = new RecyclableMemoryStreamManager();
        }

        public async Task InvokeAsync(HttpContext context)
        {
            //Request handling comes here
            var requestLog = await LogRequest(context);
            var responseLog = await LogResponse(context);

            var timeSpent = responseLog.FinishTime.Subtract(requestLog.StartTime);
            //Build General WatchLog, Join from requestLog and responseLog

            var watchLog = new WatchLog
            {
                IpAddress = context.Connection.RemoteIpAddress.ToString(),
                ResponseStatus = responseLog.ResponseStatus,
                QueryString = requestLog.QueryString,
                Method = requestLog.Method,
                Path = requestLog.Path,
                Host = requestLog.Host,
                RequestBody = requestLog.RequestBody,
                ResponseBody = responseLog.ResponseBody,
                TimeSpent = string.Format("{0:D1} hrs {1:D1} mins {2:D1} secs {3:D1} ms", timeSpent.Hours, timeSpent.Minutes, timeSpent.Seconds, timeSpent.Milliseconds),
                RequestHeaders = requestLog.Headers,
                ResponseHeaders = responseLog.Headers
            };

            Console.WriteLine("IP IS: " + watchLog.IpAddress);
            LiteDBHelper.Insert(watchLog);
            //db.Add(videogameRatings);
        }

        private async Task<RequestModel> LogRequest(HttpContext context)
        {
            var startTime = DateTime.Now;
            List<string> requestHeaders = new List<string>();
            var requestBody = string.Empty;

            var requestBodyDto = new RequestModel()
            {
                RequestBody = requestBody,
                Host = context.Request.Host.ToString(),
                Path = context.Request.Path.ToString(),
                Method = context.Request.Method.ToString(),
                QueryString = context.Request.QueryString.ToString(),
                StartTime = startTime,
                Headers = context.Request.Headers.Select(x => x.ToString()).Aggregate((a, b) => a + ": " + b),
            };

            if (context.Request.Method == "POST")
            {
                context.Request.EnableBuffering();
                await using var requestStream = _recyclableMemoryStreamManager.GetStream();
                await context.Request.Body.CopyToAsync(requestStream);
                requestBody = ReadStreamInChunks(requestStream);

                context.Request.Body.Position = 0;
            }
            
            _logger.LogInformation($"Http Request Information:{Environment.NewLine}" +
                                   $"Schema:{context.Request.Scheme} " +
                                   $"Host: {context.Request.Host} " +
                                   $"Method: {context.Request.Method}" +
                                   $"Path: {context.Request.Path} " +
                                   $"QueryString: {context.Request.QueryString} " +
                                   $"Request Body: {requestBody}");

            return requestBodyDto;
        }

        private async Task<ResponseModel> LogResponse(HttpContext context)
        {
            var responseBody = string.Empty;
            using (var originalBodyStream = context.Response.Body)
            {
                try
                {
                    using (var originalResponseBody = _recyclableMemoryStreamManager.GetStream())
                    {
                        context.Response.Body = originalResponseBody;
                        await _next(context);
                        context.Response.Body.Seek(0, SeekOrigin.Begin);
                        responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
                        context.Response.Body.Seek(0, SeekOrigin.Begin);
                        _logger.LogInformation($"Http Response Information:{Environment.NewLine}" +
                                               $"Schema:{context.Request.Scheme} " +
                                               $"Host: {context.Request.Host} " +
                                               $"Path: {context.Request.Path} " +
                                               $"QueryString: {context.Request.QueryString} " +
                                               $"Response Body: {responseBody}");
                        var responseBodyDto = new ResponseModel
                        {
                            ResponseBody = responseBody,
                            ResponseStatus = context.Response.StatusCode,
                            FinishTime = DateTime.Now,
                            Headers = context.Response.StatusCode != 200 || context.Response.StatusCode != 201 ? "" : context.Response.Headers.Select(x => x.ToString()).Aggregate((a, b) => a + ": " + b),
                        };
                        await originalResponseBody.CopyToAsync(originalBodyStream);
                        return responseBodyDto;
                    }
                }
                finally
                {
                    context.Response.Body = originalBodyStream;
                }
            }
        }




        private static string ReadStreamInChunks(Stream stream)
        {
            const int readChunkBufferLength = 4096;
            stream.Seek(0, SeekOrigin.Begin);
            using var textWriter = new StringWriter();
            using var reader = new StreamReader(stream);
            var readChunk = new char[readChunkBufferLength];
            int readChunkLength;
            do
            {
                readChunkLength = reader.ReadBlock(readChunk,
                                                   0,
                                                   readChunkBufferLength);
                textWriter.Write(readChunk, 0, readChunkLength);
            } while (readChunkLength > 0);
            return textWriter.ToString();
        }
    }
}

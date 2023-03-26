using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RCK.CloudPlatform.Common.Constants;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using VSI.CloudPlaform.Core.Db;
using VSI.CloudPlatform.Common;
using VSI.CloudPlatform.Core.Functions;

namespace FunctionApp.RCK.ECOM.OrderReceive
{
    public static class ECOMOrderReceiveAPI
    {
        private static readonly string tenantId = Environment.GetEnvironmentVariable("TenantId");
        private static readonly string InstrumentationKey = Environment.GetEnvironmentVariable("APPINSIGHTS_INSTRUMENTATIONKEY");
        private static readonly string ArchiveBlobContainer = Environment.GetEnvironmentVariable("ArchiveBlobContainer");
        private static readonly string commonStorageConnetionString = Environment.GetEnvironmentVariable("CommonStorageConnetionString");
        private static readonly int cacheExpireTimeout = FunctionUtilities.GetIntValue(Environment.GetEnvironmentVariable("CacheTimeout"), 360);
        private static readonly int flowCacheExpireTimeout = FunctionUtilities.GetIntValue(Environment.GetEnvironmentVariable("FlowCacheExpireTimeout"), 720);
        private static readonly string processNameXmlPath = Environment.GetEnvironmentVariable("ProcessNameXmlPath");

        [FunctionName("FUNC_ECOM_OrderReceive")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Anonymous, "POST", Route = null)] HttpRequest req, ILogger log)
        {
            IOperationHolder<RequestTelemetry> operation = null;

            var telemetryClient = Helper.GetTelemetryClient($"{RCKFunctionNames.FUNC_RCK_ECOM_XML_API}", InstrumentationKey, out operation);

            try
            {
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, $"Tenant ID should be defined in Function APP Configurations!");
                    telemetryClient.TrackException(new Exception("Tenant ID should be defined in Function APP Configurations!"));

                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(JsonConvert.SerializeObject(new ApiResponse { Success = false, DeveloperMessage = $"Tenant ID should be added in PartnerLinQ API Configuration, Please contact PartnerLinQ adaministrator!", FriendlyMessage = "Something went wrong while processing your request, Please contact administrator for more details!" })) };
                }

                var xml = await req.ReadAsStringAsync();

                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, "Starting...");

                var tenant = TenantTableHelper.GetTenantModel(commonStorageConnetionString, tenantId, cacheExpireTimeout, telemetryClient);

                if (tenant == null)
                {
                    telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, $"Tenant {tenantId} is not configured!");
                    telemetryClient.TrackException(new Exception($"Tenant {tenantId} is not configured!"));

                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(JsonConvert.SerializeObject(new ApiResponse { Success = false, DeveloperMessage = $"Tenant {tenantId} is not configured on PartnerLinQ!", FriendlyMessage = "Something went wrong while processing your request, Please contact administrator for more details!" })) };
                }

                var processName = FunctionHelper.GetKeyIdentifierFromXML(xml, processNameXmlPath);

                if (string.IsNullOrWhiteSpace(processName))
                {
                    telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, $"ProcessName value is required!");
                    telemetryClient.TrackException(new Exception($"ProcessName value is required!"));

                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(JsonConvert.SerializeObject(new ApiResponse { Success = false, DeveloperMessage = $"ProcessName value is required!", FriendlyMessage = "Something went wrong while processing your request, Please contact administrator for more details!" })) };
                }

                var processflow = FunctionHelper.GetProcessFlow($"{tenantId}-ProcessFlow-{processName}", flowCacheExpireTimeout, processName, telemetryClient, tenant.CloudDb);

                var stepParams = processflow.Functions.Where(f => f.StepOrder == 1).FirstOrDefault();

                var partnerShip = FunctionHelper.GetPartnership($"{tenantId}-Partnership-{stepParams.PartnershipId}", flowCacheExpireTimeout, stepParams.PartnershipId, telemetryClient, tenant.CloudDb);

                var apiParams = FunctionHelper.GetApiParams(stepParams, partnerShip);

                var stage = FunctionHelper.EdiStageObject(apiParams);

                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, $"Uploading Original message to blob storage.");

                var blobPath = await tenant.AzureBlob.UploadFileToBlobStorageAsync(xml, $"{apiParams.PartnershipCode}/{stage.Transaction_Id}/{apiParams.TransactionStep}.txt", ArchiveBlobContainer);

                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, "Pushing Success Message.");

                if (!string.IsNullOrEmpty(apiParams.KeyIdentifier))
                {
                    stage.ISAControlNum = FunctionHelper.GetKeyIdentifierFromXML(xml, apiParams.KeyIdentifier);
                }

                stage.id = FunctionHelper.LogSuccessStageMessage(tenant.CloudDb, stage, apiParams, blobPath, telemetryClient, cacheExpireTimeout);

                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, "Pushed Success Message.");

                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, $"Pushing message in Topic {apiParams.TargetTopic}.");

                await Helper.AddToTopicAsync(tenant.ServicebusConnectionString, apiParams.TargetTopic, JsonConvert.SerializeObject(stage), null);

                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, "Pushed EDI message in Topic.");

                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, "Finished.");

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(new ApiResponse { Success = true, DeveloperMessage = string.Empty, FriendlyMessage = "Order successfully pushed to proceed!" })) };
            }
            catch (Exception ex)
            {
                telemetryClient.TrackTrace(RCKFunctionNames.FUNC_RCK_ECOM_XML_API, $"an error occurres {ex}");
                telemetryClient.TrackException(ex);

                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent(JsonConvert.SerializeObject(new ApiResponse { Success = false, DeveloperMessage = ex.Message, FriendlyMessage = "Something went wrong while processing your request, Please contact administrator for more details!" })) };
            }
            finally
            {
                telemetryClient.StopOperation(operation);
            }
        }
    }

    public class ApiResponse
    {
        public bool Success { get; set; }
        public string DeveloperMessage { get; set; }
        public string FriendlyMessage { get; set; }
    }
}
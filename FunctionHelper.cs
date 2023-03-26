using Microsoft.ApplicationInsights;
using Microsoft.Azure.Documents;
using RCK.CloudPlatform.Model.SalesOrder;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Xml;
using VSI.CloudPlaform.Core.Db;
using VSI.CloudPlatform.Common;
using VSI.CloudPlatform.Core.Db;
using VSI.CloudPlatform.Core.Functions;
using VSI.CloudPlatform.Db;
using VSI.CloudPlatform.Model;
using VSI.CloudPlatform.Model.Jobs;
using VSI.Common;
using VSI.Model;
using static VSI.Contants.Global;

namespace FunctionApp.RCK.ECOM.OrderReceive
{
    public class FunctionHelper
    {
        private static readonly string tenantId = Environment.GetEnvironmentVariable("TenantId");
        private static readonly string commonStorageConnetionString = Environment.GetEnvironmentVariable("CommonStorageConnetionString");

        internal static EDI_STAGING EdiStageObject(ApiParams apiParams)
        {
            var stageData = new EDI_STAGING
            {
                Transaction_Id = Utilities.GetTransactionId(),
                Transaction_Set_Id = apiParams.TransactionSetId,
                Transaction_Set_Code = apiParams.TransactionSetCode,
                Customer_Id = apiParams.PartnershipCustomerId,
                Status = Status.Active,
                Created_Date = DateTime.Now,
                Direction = TransactionDirection.Inbound,
                PartnerShip_Code = apiParams.PartnershipCode,
                Customer_Code = apiParams.PartnerShipCustomerCode,
                Company_Code = apiParams.PartnerShipCompanyCode,
                Partnership_Id = apiParams.PartnershipId,
                Company_Id = apiParams.PartnerShipCompanyId,
                State = TransactionState.StagingStates.Candidate,
                ISAControlNum = Utilities.GetISAControlNumber().ToString(),
                EPOCCreatedDateTime = Utilities.ConvertToUnixTimestamp(DateTime.Now),
                HasBlobPaths = true,
                NoOfSteps = apiParams.TotalSteps,
                BatchId = Utilities.GetTransactionId(),
                TransactionName = apiParams.TransactionName,
                TransactionType = apiParams.TransactionType,
                StepsDetails = new List<Steps>()
                {
                    new Steps
                    {
                        StepName = apiParams.TransactionStep,
                        StartDate = DateTime.Now.ToString(),
                        StepOrder = 1,
                        LastStep = false
                    }
                }
            };
            stageData.GS06 = stageData.ISAControlNum;

            return stageData;
        }

        internal static ApiParams GetApiParams(Function stepParams, PartnerShip partnership)
        {
            var functionSettings = stepParams.Settings;

            return new ApiParams
            {
                TransactionStep = stepParams.TransactionStep,
                TransactionName = stepParams.TransactionName,
                PartnershipCode = partnership.Partnership_Code,
                PartnerShipCompanyCode = partnership.Company_Code,
                PartnerShipCompanyId = partnership.Company_Id,
                PartnerShipCustomerCode = partnership.Customer_Code,
                PartnershipCustomerId = partnership.Customer_Id,
                PartnershipId = partnership.Partnership_Id,
                TotalSteps = stepParams.TotalSteps,
                TransactionSetCode = partnership.Transaction_Set_Code,
                TransactionSetId = partnership.Transaction_Set_Id,
                TransactionType = stepParams.TransactionType,

                KeyIdentifier = functionSettings.GetValue("KeyIdentifier", ""),
                TargetTopic = functionSettings.GetValue("Service Bus Target Topic", "")
            };
        }

        internal static ProcessFlow GetProcessFlowByName(int cacheExpire, string processName, ICloudDb cloudDb, TelemetryClient telemetryClient)
        {
            try
            {
                return cloudDb.ExecuteQuery("process_flow", "SELECT * FROM c where c.entity_name = 'flow' and c.Name = '" + processName + "'").ToObject<List<ProcessFlow>>().FirstOrDefault();
            }
            catch (Exception ex)
            {
                if (ex is AggregateException aggregateException)
                {
                    var anyDocumentClientException = aggregateException.InnerExceptions.Any(e => e is DocumentClientException);

                    if (anyDocumentClientException)
                    {
                        var tenant = TenantTableHelper.GetTenantModel(commonStorageConnetionString, tenantId, cacheExpire, telemetryClient);

                        return tenant.CloudDb.ExecuteQuery("process_flow", "SELECT * FROM c where c.entity_name = 'flow' and c.Name = '" + processName + "'").ToObject<List<ProcessFlow>>().FirstOrDefault();
                    }
                }

                throw;
            }
        }

        internal static ProcessFlow GetProcessFlow(string cacheKey, int expire, string processName, TelemetryClient telemetryClient, ICloudDb cloudDb)
        {
            var cache = MemoryCache.Default;

            if (cache.Contains(cacheKey) && cache[cacheKey] != null)
            {
                telemetryClient.TrackTrace($"{cacheKey} - Getting ProcessFlow From Cache");

                return cache.Get(cacheKey) as ProcessFlow;
            }

            var processFlow = GetProcessFlowByName(expire, processName, cloudDb, telemetryClient);

            cache.Add(cacheKey, processFlow, new CacheItemPolicy() { AbsoluteExpiration = DateTime.Now.AddMinutes(expire) });

            return processFlow;
        }

        internal static PartnerShip GetPartnership(string cacheKey, int expire, string partnershipId, TelemetryClient telemetryClient, ICloudDb cloudDb)
        {
            var cache = MemoryCache.Default;

            if (cache.Contains(cacheKey) && cache[cacheKey] != null)
            {
                telemetryClient.TrackTrace($"{cacheKey} - Getting Partnership From Cache");

                return cache.Get(cacheKey) as PartnerShip;
            }

            try
            {
                var partnership = PartnershipDbHelper.GetPartnershipByPartnershipId(partnershipId, cloudDb).ToObject<List<PartnerShip>>().FirstOrDefault();

                cache.Add(cacheKey, partnership, new CacheItemPolicy() { AbsoluteExpiration = DateTime.Now.AddMinutes(expire) });

                return partnership;
            }
            catch (Exception ex)
            {
                if (ex is AggregateException aggregateException)
                {
                    var anyDocumentClientException = aggregateException.InnerExceptions.Any(e => e is DocumentClientException);

                    if (anyDocumentClientException)
                    {
                        var tenant = TenantTableHelper.GetTenantModel(commonStorageConnetionString, tenantId, expire, telemetryClient);

                        var partnership = PartnershipDbHelper.GetPartnershipByPartnershipId(partnershipId, tenant.CloudDb).ToObject<List<PartnerShip>>().FirstOrDefault();

                        cache.Add(cacheKey, partnership, new CacheItemPolicy() { AbsoluteExpiration = DateTime.Now.AddMinutes(expire) });

                        return partnership;
                    }
                }

                throw;
            }
        }

        internal static string LogSuccessStageMessage(ICloudDb cloudDb, EDI_STAGING stage, ApiParams apiParams, string blobPath, TelemetryClient telemetryClient, int cacheExpire)
        {
            stage.State = TransactionState.StagingStates.Pass;
            stage.OverallStatus = OverallStatus.IN_PROGRESS;
            stage.InboundData = blobPath;
            stage.EDI_STD_Document_ORIGINAL = blobPath;
            stage.Data = blobPath;

            var step = stage.StepsDetails.FirstOrDefault(s => s.StepName == apiParams.TransactionStep);

            step.EndDate = DateTime.Now.ToString();
            step.Status = MessageStatus.COMPLETED;
            step.FileUrl = blobPath;

            stage.EDI_STANDARD_DOCUMENT = blobPath;

            try
            {
                return StageDataDbHelper.AddStageData(stage, cloudDb);
            }
            catch (Exception ex)
            {
                if (ex is AggregateException aggregateException)
                {
                    var anyDocumentClientException = aggregateException.InnerExceptions.Any(e => e is DocumentClientException);

                    if (anyDocumentClientException)
                    {
                        var tenant = TenantTableHelper.GetTenantModel(commonStorageConnetionString, tenantId, cacheExpire, telemetryClient);

                        return StageDataDbHelper.AddStageData(stage, tenant.CloudDb);
                    }
                }

                throw;
            }

        }

        internal static string GetKeyIdentifierFromXML(string xmlContent, string keyIdentifier)
        {
            var document = new XmlDocument();
            document.LoadXml(xmlContent);

            var dataKey = document?.SelectSingleNode(keyIdentifier)?.InnerText;

            return string.IsNullOrWhiteSpace(dataKey) ? string.Empty : dataKey;
        }
    }
}
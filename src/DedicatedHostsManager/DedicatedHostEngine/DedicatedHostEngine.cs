﻿using DedicatedHostsManager.ComputeClient;
using DedicatedHostsManager.DedicatedHostStateManager;
using DedicatedHostsManager.Sync;
using Microsoft.Azure.Management.Compute;
using Microsoft.Azure.Management.Compute.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;
using Microsoft.Rest.Azure;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;
using Polly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SubResource = Microsoft.Azure.Management.Compute.Models.SubResource;

namespace DedicatedHostsManager.DedicatedHostEngine
{
    public class DedicatedHostEngine : IDedicatedHostEngine
    {
        private readonly ILogger<DedicatedHostEngine> _logger;
        private readonly IConfiguration _configuration;
        private readonly IDedicatedHostSelector _dedicatedHostSelector;
        private readonly ISyncProvider _syncProvider;
        private readonly IDedicatedHostStateManager _dedicatedHostStateManager;
        private readonly IDhmComputeClient _dhmComputeClient;

        public DedicatedHostEngine(
            ILogger<DedicatedHostEngine> logger, 
            IConfiguration configuration,
            IDedicatedHostSelector dedicatedHostSelector,
            ISyncProvider syncProvider,
            IDedicatedHostStateManager dedicatedHostStateManager,
            IDhmComputeClient dhmComputeClient)
        {
            _logger = logger;
            _configuration = configuration;
            _dedicatedHostSelector = dedicatedHostSelector;
            _syncProvider = syncProvider;
            _dedicatedHostStateManager = dedicatedHostStateManager;
            _dhmComputeClient = dhmComputeClient;
        }

        public async Task<AzureOperationResponse<DedicatedHostGroup>> CreateDedicatedHostGroup(
            string token,
            string cloudName,
            string tenantId,
            string subscriptionId, 
            string resourceGroup, 
            string dhgName,
            string azName,
            int platformFaultDomainCount,
            string location)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (string.IsNullOrEmpty(cloudName))
            {
                throw new ArgumentNullException(nameof(cloudName));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            if (string.IsNullOrEmpty(subscriptionId))
            {
                throw new ArgumentException(nameof(subscriptionId));
            }

            if (string.IsNullOrEmpty(resourceGroup))
            {
                throw new ArgumentException(nameof(resourceGroup));
            }

            if (string.IsNullOrEmpty(dhgName))
            {
                throw new ArgumentException(nameof(dhgName));
            }

            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentException(nameof(location));
            }

            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                AzureEnvironment.FromName(cloudName));
        
            var newDedicatedHostGroup = new DedicatedHostGroup()
            {
                Location = location,
                PlatformFaultDomainCount = platformFaultDomainCount
            };

            if (!string.IsNullOrEmpty(azName))
            {
                newDedicatedHostGroup.Zones = new List<string>{ azName };
            }

            var dhgCreateRetryCount = int.Parse(_configuration["DhgCreateRetryCount"]);
            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId, 
                azureCredentials,
                AzureEnvironment.FromName(cloudName));
            var response = new AzureOperationResponse<DedicatedHostGroup>();
            await Policy
                .Handle<CloudException>()
                .WaitAndRetryAsync(
                    dhgCreateRetryCount,
                    r => TimeSpan.FromSeconds(2 * r),
                    (ex, ts, r) =>
                        _logger.LogInformation(
                            $"Create host group {dhgName} failed. Attempt #{r}/{dhgCreateRetryCount}. Will try again in {ts.TotalSeconds} seconds. Exception={ex}"))
                .ExecuteAsync(async () =>
                {
                    response = await computeManagementClient.DedicatedHostGroups.CreateOrUpdateWithHttpMessagesAsync(
                        resourceGroup,
                        dhgName,
                        newDedicatedHostGroup,
                        null,
                        default(CancellationToken));
                });

            return response;
        }
      
        public async Task<AzureOperationResponse<DedicatedHost>> CreateDedicatedHost(
            string token,
            string cloudName,
            string tenantId,
            string subscriptionId, 
            string resourceGroup, 
            string dhgName, 
            string dhName,
            string dhSku,
            string location)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (string.IsNullOrEmpty(cloudName))
            {
                throw new ArgumentNullException(nameof(cloudName));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            if (string.IsNullOrEmpty(subscriptionId))
            {
                throw new ArgumentException(nameof(subscriptionId));
            }

            if (string.IsNullOrEmpty(resourceGroup))
            {
                throw new ArgumentException(nameof(resourceGroup));
            }

            if (string.IsNullOrEmpty(dhgName))
            {
                throw new ArgumentException(nameof(dhgName));
            }

            if (string.IsNullOrEmpty(dhName))
            {
                throw new ArgumentException(nameof(dhName));
            }

            if (string.IsNullOrEmpty(dhSku))
            {
                throw new ArgumentException(nameof(dhSku));
            }

            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentException(nameof(location));
            }

            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                AzureEnvironment.FromName(cloudName));

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                AzureEnvironment.FromName(cloudName));
            if (await computeManagementClient.DedicatedHostGroups.GetAsync(resourceGroup, dhgName) == null)
            {
                await computeManagementClient.DedicatedHostGroups.CreateOrUpdateAsync(
                    resourceGroup,
                    dhgName,
                    new DedicatedHostGroup()
                    {
                        Location = location,
                        PlatformFaultDomainCount = 1
                    });
            }

            return await computeManagementClient.DedicatedHosts.CreateOrUpdateWithHttpMessagesAsync(
                resourceGroup,
                dhgName,
                dhName,
                new DedicatedHost
                {
                    Location = location,
                    Sku = new Sku() {Name = dhSku}
                },
                null);
        }

        public async Task<VirtualMachine> CreateVmOnDedicatedHost(
            string token,
            string cloudName,
            string tenantId,
            string subscriptionId,
            string resourceGroup,
            string dhgName,
            string vmSku,
            string vmName,
            Region region,
            VirtualMachine virtualMachine)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (string.IsNullOrEmpty(cloudName))
            {
                throw new ArgumentNullException(nameof(cloudName));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            if (string.IsNullOrEmpty(subscriptionId))
            {
                throw new ArgumentException(nameof(subscriptionId));
            }

            if (string.IsNullOrEmpty(resourceGroup))
            {
                throw new ArgumentException(nameof(resourceGroup));
            }

            if (string.IsNullOrEmpty(dhgName))
            {
                throw new ArgumentException(nameof(dhgName));
            }

            if (string.IsNullOrEmpty(vmSku))
            {
                throw new ArgumentException(nameof(vmSku));
            }

            if (string.IsNullOrEmpty(vmName))
            {
                throw new ArgumentException(nameof(vmName));
            }

            if (region == null)
            {
                throw new ArgumentNullException(nameof(region));
            }

            if (virtualMachine == null)
            {
                throw new ArgumentNullException(nameof(virtualMachine));
            }

            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                AzureEnvironment.FromName(cloudName));

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                AzureEnvironment.FromName(cloudName));
            VirtualMachine response = null;
            var vmProvisioningState = virtualMachine.ProvisioningState;
            var minIntervalToCheckForVmInSeconds = int.Parse(_configuration["MinIntervalToCheckForVmInSeconds"]);
            var maxIntervalToCheckForVmInSeconds = int.Parse(_configuration["MaxIntervalToCheckForVmInSeconds"]);
            var retryCountToCheckVmState = int.Parse(_configuration["RetryCountToCheckVmState"]);
            var maxRetriesToCreateVm = int.Parse(_configuration["MaxRetriesToCreateVm"]);
            var dedicatedHostCacheTtlMin = int.Parse(_configuration["DedicatedHostCacheTtlMin"]);
            var vmCreationRetryCount = 0;
            
            while ((string.IsNullOrEmpty(vmProvisioningState)
                   || !string.Equals(vmProvisioningState, "Succeeded", StringComparison.InvariantCultureIgnoreCase))
                   && vmCreationRetryCount < maxRetriesToCreateVm)
            {             
                if (string.IsNullOrEmpty(vmProvisioningState))
                {
                    var dedicatedHostId = await GetDedicatedHostForVmPlacement(
                        token,
                        cloudName,
                        tenantId,
                        subscriptionId,
                        resourceGroup,
                        dhgName,
                        vmSku,
                        vmName,
                        region.Name);

                    _dedicatedHostStateManager.MarkHostUsage(dedicatedHostId.ToLower(), DateTimeOffset.Now.ToString(), TimeSpan.FromMinutes(dedicatedHostCacheTtlMin)); 
                    virtualMachine.Host = new SubResource(dedicatedHostId);
                    try
                    {
                        response = await computeManagementClient.VirtualMachines
                            .CreateOrUpdateAsync(
                                resourceGroup,
                                vmName,
                                virtualMachine);
                    }
                    catch (CloudException cloudException)
                    {
                        if (!string.IsNullOrEmpty(cloudException.Body?.Code) && string.Equals(cloudException.Body?.Code, "AllocationFailed"))
                        {
                            // do nothing, retry when we hit a allocation issue related to capacity
                        }
                        else
                        {
                            throw;
                        }
                    }
                }

                // TODO: Remove below if block once the Compute DH stops provisioning VMs in F state when capacity bound.
                // TODO: Intentional code duplication below to keep logic related to this bug separate.
                if (string.Equals(vmProvisioningState, "Failed", StringComparison.InvariantCultureIgnoreCase))
                {
                    _logger.LogMetric("VmProvisioningFailureCountMetric", 1);
                    _dedicatedHostStateManager.MarkHostAtCapacity(virtualMachine.Host.Id.ToLower(), DateTimeOffset.Now.ToString(), TimeSpan.FromMinutes(dedicatedHostCacheTtlMin)); 
                    var dedicatedHostId = await GetDedicatedHostForVmPlacement(
                        token,
                        cloudName,
                        tenantId,
                        subscriptionId,
                        resourceGroup,
                        dhgName,
                        vmSku,
                        vmName,
                        region.Name);

                    await computeManagementClient.VirtualMachines.DeallocateAsync(resourceGroup, virtualMachine.Name);
                    virtualMachine.Host = new SubResource(dedicatedHostId);
                    response = await computeManagementClient.VirtualMachines
                        .CreateOrUpdateAsync(
                            resourceGroup,
                            vmName,
                            virtualMachine);
                    try
                    {
                        await computeManagementClient.VirtualMachines.StartAsync(resourceGroup, virtualMachine.Name);
                    }
                    catch (CloudException cloudException)
                    {
                        if (!string.IsNullOrEmpty(cloudException.Body?.Code) && string.Equals(cloudException.Body?.Code, "AllocationFailed"))
                        {
                            // do nothing, retry when we hit a allocation issue related to capacity
                        }
                        else
                        {
                            throw;
                        }
                    }
                }

                // VM provisioning takes a few seconds, wait for provisioning state to update
                await Task.Delay(TimeSpan.FromSeconds(new Random().Next(minIntervalToCheckForVmInSeconds, maxIntervalToCheckForVmInSeconds)));
                await Policy
                    .Handle<CloudException>()
                    .WaitAndRetryAsync(
                        retryCountToCheckVmState, 
                        r => TimeSpan.FromSeconds(2 * r),
                        onRetry: (ex, ts, r) =>
                            _logger.LogInformation(
                                $"Could not get provisioning state for {virtualMachine.Name}. Attempt #{r}/{retryCountToCheckVmState}. Will try again in {ts.TotalSeconds} seconds. Exception={ex}"))
                    .ExecuteAsync(async () =>
                    {
                        vmProvisioningState =
                            (await computeManagementClient.VirtualMachines.GetAsync(resourceGroup, virtualMachine.Name))
                            .ProvisioningState;
                    });

                _logger.LogInformation($"Provisioning state for {virtualMachine.Name} is {vmProvisioningState}");
                vmCreationRetryCount++;
            }

            return response;
        }

        public async Task<string> GetDedicatedHostForVmPlacement(
            string token,
            string cloudName,
            string tenantId,
            string subscriptionId,
            string resourceGroup,
            string hostGroupName,
            string requiredVmSize,
            string vmName,
            string location)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (string.IsNullOrEmpty(cloudName))
            {
                throw new ArgumentNullException(nameof(cloudName));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            if (string.IsNullOrEmpty(subscriptionId))
            {
                throw new ArgumentException(nameof(subscriptionId));
            }

            if (string.IsNullOrEmpty(resourceGroup))
            {
                throw new ArgumentException(nameof(resourceGroup));
            }

            if (string.IsNullOrEmpty(hostGroupName))
            {
                throw new ArgumentException(nameof(hostGroupName));
            }

            if (string.IsNullOrEmpty(requiredVmSize))
            {
                throw new ArgumentException(nameof(requiredVmSize));
            }

            if (string.IsNullOrEmpty(vmName))
            {
                throw new ArgumentException(nameof(vmName));
            }

            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentException(nameof(location));
            }

            var matchingHostId = string.Empty;
            var innerLoopStopwatch = Stopwatch.StartNew();

            while (string.IsNullOrEmpty(matchingHostId))
            {
                matchingHostId = await _dedicatedHostSelector.SelectDedicatedHost(
                    token,
                    cloudName,
                    tenantId,
                    subscriptionId,
                    resourceGroup,
                    hostGroupName,
                    requiredVmSize);

                if (string.IsNullOrEmpty(matchingHostId))
                {
                    var lockRetryCount = int.Parse(_configuration["LockRetryCount"]);
                    var hostGroupId = await GetDedicatedHostGroupId(
                        token,
                        cloudName,
                        tenantId,
                        subscriptionId,
                        resourceGroup,
                        hostGroupName);

                    await Policy
                        .Handle<StorageException>()
                        .WaitAndRetryAsync(
                            lockRetryCount, 
                            r => TimeSpan.FromSeconds(2 * r),
                            (ex, ts, r) => _logger.LogInformation($"Attempt #{r.Count}/{lockRetryCount}. Will try again in {ts.TotalSeconds} seconds. Exception={ex}"))
                        .ExecuteAsync(async () =>
                        {
                            try
                            {
                                _logger.LogInformation($"About to lock");
                                await _syncProvider.StartSerialRequests(hostGroupId);

                                matchingHostId = await _dedicatedHostSelector.SelectDedicatedHost(
                                    token,
                                    cloudName,
                                    tenantId,
                                    subscriptionId,
                                    resourceGroup,
                                    hostGroupName,
                                    requiredVmSize);

                                if (string.IsNullOrEmpty(matchingHostId))
                                {
                                    _logger.LogInformation($"Creating a new host.");
                                    var vmToHostDictionary = JsonConvert.DeserializeObject<Dictionary<string, string>>(_configuration["VmToHostMapping"]);
                                    if (vmToHostDictionary == null || string.IsNullOrEmpty(vmToHostDictionary[requiredVmSize]))
                                    {
                                        throw new Exception($"Cannot find a dedicated host SKU for the {requiredVmSize}: vm to host mapping was null.");
                                    }

                                    var hostSku = vmToHostDictionary[requiredVmSize];
                                    _logger.LogInformation($"Host SKU {hostSku} will be used to host VM SKU {requiredVmSize}.");
                                    if (string.IsNullOrEmpty(hostSku))
                                    {
                                        throw new Exception(
                                            $"Cannot find a dedicated host SKU for the {requiredVmSize}: vm to host mapping was null.");
                                    }

                                    var newDedicatedHostResponse = await CreateDedicatedHost(
                                        token,
                                        cloudName,
                                        tenantId,
                                        subscriptionId,
                                        resourceGroup,
                                        hostGroupName,
                                        "host-" + (new Random().Next(100,999)),
                                        hostSku,
                                        location);

                                    matchingHostId = newDedicatedHostResponse.Body.Id;
                                    _logger.LogMetric("DedicatedHostCreationCountMetric", 1);
                                }
                            }
                            catch (Exception exception)
                            {
                                _logger.LogError($"Error while finding a DH: {exception}");
                            }
                            finally
                            {
                                _logger.LogInformation($"Releasing the lock");
                                await _syncProvider.EndSerialRequests(hostGroupId);
                            }
                        });
                }

                _logger.LogInformation($"Retry to find a host for {vmName} of SKU {requiredVmSize}");
            }

            if (string.IsNullOrEmpty(matchingHostId))
            {
                _logger.LogError($"Something went really wrong! Could not find a " +
                                 $"matching host for {requiredVmSize} within {innerLoopStopwatch.Elapsed.TotalSeconds} seconds. ");
            }

            _logger.LogMetric("GetDedicatedHostTimeSecondsMetric", innerLoopStopwatch.Elapsed.TotalSeconds);
            _logger.LogInformation($"GetDedicatedHost: Took {innerLoopStopwatch.Elapsed.TotalSeconds} seconds to find a matching host {matchingHostId} for {vmName} of {requiredVmSize} SKU.");
            return matchingHostId;
        }

        private async Task<string> GetDedicatedHostGroupId(
            string token,
            string cloudName,
            string tenantId,
            string subscriptionId,
            string resourceGroupName, 
            string hostGroupName)
        {
            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                AzureEnvironment.FromName(cloudName));

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                AzureEnvironment.FromName(cloudName));
            return (await computeManagementClient.DedicatedHostGroups.GetAsync(resourceGroupName, hostGroupName)).Id;
        }

        public async Task<IList<DedicatedHostGroup>> ListDedicatedHostGroups(
            string token,
            string cloudName,
            string tenantId,
            string subscriptionId)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (string.IsNullOrEmpty(cloudName))
            {
                throw new ArgumentNullException(nameof(cloudName));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                AzureEnvironment.FromName(cloudName));

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                AzureEnvironment.FromName(cloudName));
            var dedicatedHostGroups = new List<DedicatedHostGroup>();
            var dedicatedHostGroupResponse =
                await computeManagementClient.DedicatedHostGroups.ListBySubscriptionAsync();
            dedicatedHostGroups.AddRange(dedicatedHostGroupResponse.ToList());

            var nextLink = dedicatedHostGroupResponse.NextPageLink;
            while (!string.IsNullOrEmpty(nextLink))
            {
                dedicatedHostGroupResponse =
                    await computeManagementClient.DedicatedHostGroups.ListBySubscriptionNextAsync(nextLink);
                dedicatedHostGroups.AddRange(dedicatedHostGroups.ToList());
                nextLink = dedicatedHostGroupResponse.NextPageLink;
            }

            return dedicatedHostGroups;
        }

        public async Task DeleteVmOnDedicatedHost(
            string token,
            string cloudName,
            string tenantId,
            string subscriptionId,
            string resourceGroup,
            string dedicatedHostGroup,
            string vmName)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (string.IsNullOrEmpty(cloudName))
            {
                throw new ArgumentNullException(nameof(cloudName));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            if (string.IsNullOrEmpty(subscriptionId))
            {
                throw new ArgumentNullException(nameof(subscriptionId));
            }

            if (string.IsNullOrEmpty(resourceGroup))
            {
                throw new ArgumentNullException(nameof(resourceGroup));
            }

            if (string.IsNullOrEmpty(dedicatedHostGroup))
            {
                throw new ArgumentNullException(nameof(dedicatedHostGroup));
            }

            if (string.IsNullOrEmpty(vmName))
            {
                throw new ArgumentNullException(nameof(vmName));
            }

            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                AzureEnvironment.FromName(cloudName));

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                AzureEnvironment.FromName(cloudName));
            var retryCountToCheckVm = int.Parse(_configuration["RetryCountToCheckVmState"]);
            var dedicatedHostCacheTtlMin = int.Parse(_configuration["DedicatedHostCacheTtlMin"]);
            VirtualMachine virtualMachine = null;
            DedicatedHost dedicatedHost = null;
            string hostId = null;
            await Policy
                .Handle<CloudException>()
                .WaitAndRetryAsync(
                    retryCountToCheckVm,
                    r => TimeSpan.FromSeconds(2 * r),
                    onRetry: (ex, ts, r) =>
                        _logger.LogInformation(
                            $"Could not get VM details for {vmName}. Attempt #{r}/{retryCountToCheckVm}. Will try again in {ts.TotalSeconds} seconds. Exception={ex}"))
                .ExecuteAsync(async () =>
                    {
                        virtualMachine = await computeManagementClient.VirtualMachines.GetAsync(resourceGroup, vmName);
                        hostId = virtualMachine?.Host?.Id;
                        var hostName = hostId?.Split(new[] {'/'}).Last();
                        await computeManagementClient.VirtualMachines.DeleteAsync(resourceGroup, vmName);
                        dedicatedHost = await computeManagementClient.DedicatedHosts.GetAsync(resourceGroup, dedicatedHostGroup, hostName, InstanceViewTypes.InstanceView);
                    });

            if (string.IsNullOrEmpty(hostId))
            {
                _logger.LogInformation($"Could not find Host for {vmName}.");
                return;
            }

            if (dedicatedHost?.VirtualMachines.Count == 0)
            {
                // Avoid locking for now; revisit if needed
                _dedicatedHostStateManager.MarkHostForDeletion(hostId.ToLower(), DateTimeOffset.Now.ToString(), TimeSpan.FromMinutes(dedicatedHostCacheTtlMin)); 
                if (!_dedicatedHostStateManager.IsHostInUsage(hostId.ToLower()))
                {
                    await computeManagementClient.DedicatedHosts.DeleteAsync(resourceGroup, dedicatedHostGroup, dedicatedHost.Name);
                    _dedicatedHostStateManager.UnmarkHostUsage(hostId.ToLower());
                }
            }
        }
    }
}
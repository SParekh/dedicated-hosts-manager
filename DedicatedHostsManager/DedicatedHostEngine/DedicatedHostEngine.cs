﻿using DedicatedHostsManager.ComputeClient;
using DedicatedHostsManager.DedicatedHostStateManager;
using DedicatedHostsManager.Sync;
using Microsoft.Azure.Management.Compute;
using Microsoft.Azure.Management.Compute.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;
using Microsoft.Rest.Azure;
using Microsoft.WindowsAzure.Storage;
using Polly;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SubResource = Microsoft.Azure.Management.Compute.Models.SubResource;

namespace DedicatedHostsManager.DedicatedHostEngine
{
    /// <summary>
    /// The Dedicated Host Engine managed host and VM (on Host) lifecycle and allocation.
    /// </summary>
    public class DedicatedHostEngine : IDedicatedHostEngine
    {
        private readonly ILogger<DedicatedHostEngine> _logger;
        private readonly Config _config;
        private readonly IDedicatedHostSelector _dedicatedHostSelector;
        private readonly ISyncProvider _syncProvider;
        private readonly IDedicatedHostStateManager _dedicatedHostStateManager;
        private readonly IDhmComputeClient _dhmComputeClient; 

        /// <summary>
        /// Initializes the Dedicated Host engine.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="configuration">Configuration.</param>
        /// <param name="dedicatedHostSelector">Dedicated Host selector.</param>
        /// <param name="syncProvider">Sync provider.</param>
        /// <param name="dedicatedHostStateManager">Dedicated Host state manager.</param>
        /// <param name="dhmComputeClient">Dedicated Host compute client.</param>
        public DedicatedHostEngine(
            ILogger<DedicatedHostEngine> logger,
            Config config,
            IDedicatedHostSelector dedicatedHostSelector,
            ISyncProvider syncProvider,
            IDedicatedHostStateManager dedicatedHostStateManager,
            IDhmComputeClient dhmComputeClient)
        {
            _logger = logger;
            _config = config;
            _dedicatedHostSelector = dedicatedHostSelector;
            _syncProvider = syncProvider;
            _dedicatedHostStateManager = dedicatedHostStateManager;
            _dhmComputeClient = dhmComputeClient;
        }

        /// <summary>
        /// Creates a Dedicated Host Group.
        /// </summary>
        /// <param name="token">Auth token.</param>
        /// <param name="azureEnvironment">Azure cloud.</param>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="subscriptionId">Subscription ID.</param>
        /// <param name="resourceGroup">Resource group.</param>
        /// <param name="dhgName">Dedicated Host group name.</param>
        /// <param name="azName">Availability zone.</param>
        /// <param name="platformFaultDomainCount">Fault domain count.</param>
        /// <param name="location">Location/region.</param>
        public async Task<AzureOperationResponse<DedicatedHostGroup>> CreateDedicatedHostGroup(
            string token,
            AzureEnvironment azureEnvironment,
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

            if (azureEnvironment == null)
            {
                throw new ArgumentNullException(nameof(azureEnvironment));
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
                azureEnvironment);

            var newDedicatedHostGroup = new DedicatedHostGroup()
            {
                Location = location,
                PlatformFaultDomainCount = platformFaultDomainCount
            };

            if (!string.IsNullOrEmpty(azName))
            {
                newDedicatedHostGroup.Zones = new List<string> { azName };
            }

            var dhgCreateRetryCount = _config.DhgCreateRetryCount;
            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                azureEnvironment);
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

        /// <summary>
        /// Creates a Dedicated Host.
        /// </summary>
        /// <param name="token">Auth token.</param>
        /// <param name="azureEnvironment">Azure cloud.</param>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="subscriptionId">Subscription ID.</param>
        /// <param name="resourceGroup">Resource group.</param>
        /// <param name="dhgName">Dedicated Host group name.</param>
        /// <param name="dhName">Dedicated Host name.</param>
        /// <param name="dhSku">Dedicated Host SKU</param>
        /// <param name="location">Azure region.</param>
        public async Task<AzureOperationResponse<DedicatedHost>> CreateDedicatedHost(
            string token,
            AzureEnvironment azureEnvironment,
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

            if (azureEnvironment == null)
            {
                throw new ArgumentNullException(nameof(azureEnvironment));
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
                azureEnvironment);

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                azureEnvironment);
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
                    Sku = new Sku() { Name = dhSku }
                },
                null);
        }

        /// <summary>
        /// Creates a VM on a Dedicated Host
        /// </summary>
        /// <param name="token">Auth token.</param>
        /// <param name="azureEnvironment">Azure cloud.</param>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="subscriptionId">Subscription ID.</param>
        /// <param name="resourceGroup">Resource group.</param>
        /// <param name="dhgName">Dedicated Host group name.</param>
        /// <param name="vmSku">VM SKU.</param>
        /// <param name="vmName">VM name.</param>
        /// <param name="region">Azure region for VM.</param>
        /// <param name="virtualMachine">VirtualMachine object (serialized).</param>
        public async Task<VirtualMachine> CreateVmOnDedicatedHost(
            string token,
            AzureEnvironment azureEnvironment,
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

            if (azureEnvironment == null)
            {
                throw new ArgumentNullException(nameof(azureEnvironment));
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
                azureEnvironment);

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                azureEnvironment);
            VirtualMachine response = null;
            var vmProvisioningState = virtualMachine.ProvisioningState;
            var minIntervalToCheckForVmInSeconds = _config.MinIntervalToCheckForVmInSeconds;
            var maxIntervalToCheckForVmInSeconds = _config.MaxIntervalToCheckForVmInSeconds;
            var retryCountToCheckVmState = _config.RetryCountToCheckVmState;
            var maxRetriesToCreateVm = _config.MaxRetriesToCreateVm;
            var dedicatedHostCacheTtlMin = _config.DedicatedHostCacheTtlMin;
            var vmCreationRetryCount = 0;

            while ((string.IsNullOrEmpty(vmProvisioningState)
                   || !string.Equals(vmProvisioningState, "Succeeded", StringComparison.InvariantCultureIgnoreCase))
                   && vmCreationRetryCount < maxRetriesToCreateVm)
            {
                if (string.IsNullOrEmpty(vmProvisioningState))
                {
                    var dedicatedHostId = await GetDedicatedHostForVmPlacement(
                        token,
                        azureEnvironment,
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

                // TODO: Remove below 'if block' once the Compute DH stops provisioning VMs in F state when capacity bound.
                // TODO: Intentional code duplication below to keep logic related to this bug separate.
                if (string.Equals(vmProvisioningState, "Failed", StringComparison.InvariantCultureIgnoreCase))
                {
                    _logger.LogMetric("VmProvisioningFailureCountMetric", 1);
                    _dedicatedHostStateManager.MarkHostAtCapacity(virtualMachine.Host.Id.ToLower(), DateTimeOffset.Now.ToString(), TimeSpan.FromMinutes(dedicatedHostCacheTtlMin));
                    var dedicatedHostId = await GetDedicatedHostForVmPlacement(
                        token,
                        azureEnvironment,
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

                // Wait for VM provisioning state to update
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

        /// <summary>
        /// Finds a Dedicated Host to host a VM.
        /// </summary>
        /// <param name="token">Auth token.</param>
        /// <param name="azureEnvironment">Azure cloud.</param>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="subscriptionId">Subscription ID.</param>
        /// <param name="resourceGroup">Resource group.</param>
        /// <param name="hostGroupName">Dedicated Host group name.</param>
        /// /// <param name="requiredVmSize">VM SKU.</param>
        /// <param name="vmName">VM name.</param>
        /// <param name="location">VM region.</param>
        public async Task<string> GetDedicatedHostForVmPlacement(
            string token,
            AzureEnvironment azureEnvironment,
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

            if (azureEnvironment == null)
            {
                throw new ArgumentNullException(nameof(azureEnvironment));
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
                    azureEnvironment,
                    tenantId,
                    subscriptionId,
                    resourceGroup,
                    hostGroupName,
                    requiredVmSize);

                if (string.IsNullOrEmpty(matchingHostId))
                {
                    var lockRetryCount = _config.LockRetryCount;
                    var hostGroupId = await GetDedicatedHostGroupId(
                        token,
                        azureEnvironment,
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
                                    azureEnvironment,
                                    tenantId,
                                    subscriptionId,
                                    resourceGroup,
                                    hostGroupName,
                                    requiredVmSize);

                                if (string.IsNullOrEmpty(matchingHostId))
                                {
                                    _logger.LogInformation($"Creating a new host.");
                                    var hostSku = GetVmToHostMapping(requiredVmSize);

                                    var newDedicatedHostResponse = await CreateDedicatedHost(
                                        token,
                                        azureEnvironment,
                                        tenantId,
                                        subscriptionId,
                                        resourceGroup,
                                        hostGroupName,
                                        "host-" + (new Random().Next(100, 999)),
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

        /// <summary>
        /// List Dedicated Host groups.
        /// </summary>
        /// <param name="token">Auth token.</param>
        /// <param name="azureEnvironment">Azure cloud.</param>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="subscriptionId">Subscription ID.</param>
        public async Task<IList<DedicatedHostGroup>> ListDedicatedHostGroups(
        string token,
            AzureEnvironment azureEnvironment,
            string tenantId,
            string subscriptionId)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (azureEnvironment == null)
            {
                throw new ArgumentNullException(nameof(azureEnvironment));
            }

            if (string.IsNullOrEmpty(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }

            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                azureEnvironment);

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                azureEnvironment);
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

        /// <summary>
        /// Deletes a VM running on a Dedicated Host, and the Host too if it does not have
        /// any more VMs running.
        /// </summary>
        /// <param name="token">Auth token.</param>
        /// <param name="azureEnvironment">Azure cloud.</param>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="subscriptionId">Subscription ID.</param>
        /// <param name="resourceGroup">Resource group.</param>
        /// <param name="dedicatedHostGroup">Dedicated Host group name.</param>
        /// <param name="vmName">VM name.</param>
        /// <returns></returns>
        public async Task DeleteVmOnDedicatedHost(
            string token,
            AzureEnvironment azureEnvironment,
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

            if (azureEnvironment == null)
            {
                throw new ArgumentNullException(nameof(azureEnvironment));
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
                azureEnvironment);

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                azureEnvironment);
            var retryCountToCheckVm = _config.RetryCountToCheckVmState;
            var dedicatedHostCacheTtlMin = _config.DedicatedHostCacheTtlMin;
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
                    var hostName = hostId?.Split(new[] { '/' }).Last();
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
                    _dedicatedHostStateManager.UnmarkHostForDeletion(hostId.ToLower());
                }
            }
        }

        public async Task<IList<DedicatedHost>> PrepareDedicatedHostGroup(
                string token,
                AzureEnvironment azureEnvironment,
                string tenantId, string subscriptionId,
                string resourceGroup,
                string dhGroupName,
                string vmSku,
                int vmInstances,
                int? platformFaultDomain)
        {
            List<DedicatedHost> dedicatedHosts = default;

            if (string.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (azureEnvironment == null)
            {
                throw new ArgumentNullException(nameof(azureEnvironment));
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

            if (string.IsNullOrEmpty(dhGroupName))
            {
                throw new ArgumentException(nameof(dhGroupName));
            }

            if (string.IsNullOrEmpty(vmSku))
            {
                throw new ArgumentException(nameof(vmSku));
            }

            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                azureEnvironment);

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                azureEnvironment);

            var dhgCreateRetryCount = _config.DhgCreateRetryCount;
            var hostGroup = await GetDedicatedHostGroup();
            var location = hostGroup.Location; // Location of DH canot be different from Host Group.
            var existingHostsOnDHGroup = await GetExistingHostsOnDHGroup();

            var (dhSku, vmCapacityPerHost) = GetVmCapacityPerHost(location, vmSku);

            var numOfDedicatedHostsByFaultDomain = this.CalculatePlatformFaultDomainToHost(
                hostGroup,
                existingHostsOnDHGroup,
                vmSku,
                vmInstances,
                vmCapacityPerHost,
                platformFaultDomain);

            await CreateDedicatedHosts();
            
            return dedicatedHosts ?? new List<DedicatedHost>();
             
            async Task<DedicatedHostGroup> GetDedicatedHostGroup()
            { 
                var response = await Helper.ExecuteAsyncWithRetry<CloudException, AzureOperationResponse<DedicatedHostGroup>>(
                            funcToexecute: () => computeManagementClient.DedicatedHostGroups.GetWithHttpMessagesAsync(resourceGroup, dhGroupName),
                            logHandler: (retryMsg) => _logger.LogInformation($"Get Dedicated Host Group '{dhGroupName} failed.' {retryMsg}"),
                            exceptionFilter: ce => !ce.Message.Contains("not found"));
                return response.Body;
            }

            async Task<List<DedicatedHost>> GetExistingHostsOnDHGroup()
            {
                var hostsInHostGroup = await this._dedicatedHostSelector.ListDedicatedHosts(
                            token,
                            azureEnvironment,
                            tenantId,
                            subscriptionId,
                            resourceGroup,
                            dhGroupName);

                var taskList = hostsInHostGroup.Select(
                    dedicatedHost => Helper.ExecuteAsyncWithRetry<CloudException, AzureOperationResponse<DedicatedHost>>(
                            () => computeManagementClient.DedicatedHosts.GetWithHttpMessagesAsync(
                                                          resourceGroup,
                                                          dhGroupName,
                                                          dedicatedHost.Name,
                                                          InstanceViewTypes.InstanceView),
                            (retryMsg) => _logger.LogInformation($"Get details for Dedicated Host '{dedicatedHost.Name} failed.' {retryMsg}"))); 
                             
                var response = await Task.WhenAll(taskList);
                return response.Select(r => r.Body).ToList();
            }

            async Task CreateDedicatedHosts()
            {
                if (numOfDedicatedHostsByFaultDomain.Any())
                {
                    var createDhHostTasks = numOfDedicatedHostsByFaultDomain
                    .SelectMany(c => Enumerable.Repeat(c.fd, c.numberOfHosts))
                    .Select(pfd => Helper.ExecuteAsyncWithRetry<CloudException, AzureOperationResponse<DedicatedHost>>(
                            funcToexecute: () => computeManagementClient.DedicatedHosts.CreateOrUpdateWithHttpMessagesAsync(
                                                resourceGroup,
                                                dhGroupName,
                                                "host-" + (new Random().Next(100, 999)),
                                                new DedicatedHost
                                                {
                                                    Location = location,
                                                    Sku = new Sku() { Name = dhSku },
                                                    PlatformFaultDomain = pfd
                                                }),
                            logHandler: (retryMsg) => _logger.LogInformation($"Create host on  Dedicated Host Group Fault Domain {pfd} failed.' {retryMsg}"),
                            retryCount: _config.DhgCreateRetryCount));

                    var bulkTask = Task.WhenAll(createDhHostTasks);
                    try
                    {
                        var response = await bulkTask;
                        dedicatedHosts = response.Select(c => c.Body).ToList();
                        _logger.LogInformation(@$"Following dedicated hosts created created successfully : {string.Join(",", dedicatedHosts.Select(d => d.Name))}");
                    }
                    catch (Exception ex)
                    {
                        if (bulkTask?.Exception?.InnerExceptions != null && bulkTask.Exception.InnerExceptions.Any())
                        {
                            throw new Exception($"Creation of Dedicated Host failed with exceptions : \n {string.Join(",\n", bulkTask.Exception.InnerExceptions.Select(c => c?.Message + "\n"))}");
                        }
                        else
                        {
                            throw new Exception($"Unexpected exception thrown {ex?.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the ID for a Dedicated Host group.
        /// </summary>
        /// <param name="token">Auth token.</param>
        /// <param name="azureEnvironment">Azure cloud.</param>
        /// <param name="tenantId">Tenant ID.</param>
        /// <param name="subscriptionId">Subscription ID.</param>
        /// <param name="resourceGroupName">Resource group.</param>
        /// <param name="hostGroupName">Dedicated Host group name.</param>
        private async Task<string> GetDedicatedHostGroupId(
            string token,
            AzureEnvironment azureEnvironment,
            string tenantId,
            string subscriptionId,
            string resourceGroupName,
            string hostGroupName)
        {
            var azureCredentials = new AzureCredentials(
                new TokenCredentials(token),
                new TokenCredentials(token),
                tenantId,
                azureEnvironment);

            var computeManagementClient = await _dhmComputeClient.GetComputeManagementClient(
                subscriptionId,
                azureCredentials,
                azureEnvironment);
            return (await computeManagementClient.DedicatedHostGroups.GetAsync(resourceGroupName, hostGroupName)).Id;
        }

        private IList<(int fd, int numberOfHosts)> CalculatePlatformFaultDomainToHost(
            DedicatedHostGroup dhGroup,
            IList<DedicatedHost> existingHostsOnDHGroup,
            string vmSku,
            int vmInstancesRequested,
            int vmCapacityPerHost,
            int? platformFaultDomain)
        {
            var dedicatedHosts = new List<(int fd, int numberOfHosts)>();
            var platformFaultDomains = DetermineFaultDomainsForPlacement(dhGroup.PlatformFaultDomainCount, platformFaultDomain);
            var vmsRequiredPerFaultDomain = (int)Math.Round((decimal)vmInstancesRequested / platformFaultDomains.Length, MidpointRounding.ToPositiveInfinity);

            foreach (var fd in platformFaultDomains)
            {
                var dhInFaultDomain = existingHostsOnDHGroup.Where(c => c.PlatformFaultDomain == fd);
                var availableVMCapacityInFaultDomain = 0;
                foreach (var host in dhInFaultDomain)
                {
                    // Existing hosts can be different sku then DH Sku determined for VM size.
                    availableVMCapacityInFaultDomain += (int)(host.InstanceView?.AvailableCapacity?.AllocatableVMs?
                        .FirstOrDefault(v => v.VmSize.Equals(vmSku, StringComparison.InvariantCultureIgnoreCase))?.Count ?? 0);
                }

                if (vmsRequiredPerFaultDomain > availableVMCapacityInFaultDomain)
                {
                    var fdHostsToBeAdded = (int)(Math.Round(((decimal)vmsRequiredPerFaultDomain - availableVMCapacityInFaultDomain) / vmCapacityPerHost, MidpointRounding.ToPositiveInfinity));
                    dedicatedHosts.Add((fd, fdHostsToBeAdded));
                    _logger.LogInformation(@$"{fdHostsToBeAdded} Hosts to be added to PlatformFaultDomain - '{fd}'");
                }
            }

            return dedicatedHosts;

            int[] DetermineFaultDomainsForPlacement(int dhGroupFaultDomainCount, int? platformFaultDomain)
            {
                if (platformFaultDomain != null && platformFaultDomain > (dhGroupFaultDomainCount - 1))
                {
                    throw new Exception($"Invalid requested Platform Fault domain -Dedicated Host Group Fault Domains = {dhGroupFaultDomainCount}, requested Platform Fault domain = {platformFaultDomain}");
                }

                return platformFaultDomain switch
                {
                    0 => new int[] { 0 },
                    1 => new int[] { 1 },
                    2 => new int[] { 2 },
                    _ => Enumerable.Range(0, dhGroupFaultDomainCount).ToArray() // As # of small, perf of using Range not significant
                };
            }
        }

        private (string dhSku, int vmCapacity) GetVmCapacityPerHost(string location, string vmSku)
        {
            var matchingConfig = _config.DedicatedHostConfigurationTable
                .Where(c => (c.Location == "default" || c.Location.Equals(location, StringComparison.OrdinalIgnoreCase))
                    && c.VmSku == vmSku);

            if (!matchingConfig.Any())
            {
                throw new Exception($"DhSku mapping not found for default OR Location {location} / VM Sku {vmSku}");
            }

            var regionSpecific = matchingConfig.SingleOrDefault(c => c.Location == location);
            if (regionSpecific != null)
            {
                return (regionSpecific.DhSku, regionSpecific.VmCapacity);
            }
            var defaultSetting = matchingConfig.Single(c => c.Location == "default");
            return (defaultSetting.DhSku, defaultSetting.VmCapacity);
        }

        private string GetVmToHostMapping(string vmSku)
        {
            var vmToHostDictionary = _config.VirtualMachineToHostMapping;
            if (vmToHostDictionary == null || string.IsNullOrEmpty(vmToHostDictionary[vmSku]))
            {
                throw new Exception($"Cannot find a dedicated host SKU for the {vmSku}: vm to host mapping was null.");
            }

            var hostSku = vmToHostDictionary[vmSku];
            _logger.LogInformation($"Host SKU {hostSku} will be used to host VM SKU {vmSku}.");
            if (string.IsNullOrEmpty(hostSku))
            {
                throw new Exception(
                    $"Cannot find a dedicated host SKU for the {vmSku}: vm to host mapping was null.");
            }

            return hostSku;
        }
    }
}

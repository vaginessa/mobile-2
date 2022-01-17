﻿using Bit.Core.Abstractions;
using Bit.Core.Models.Domain;
using System;
using System.Linq;
using System.Threading.Tasks;
using Bit.Core.Enums;

namespace Bit.Core.Services
{
    public class VaultTimeoutService : IVaultTimeoutService
    {
        private readonly ICryptoService _cryptoService;
        private readonly IStateService _stateService;
        private readonly IPlatformUtilsService _platformUtilsService;
        private readonly IFolderService _folderService;
        private readonly ICipherService _cipherService;
        private readonly ICollectionService _collectionService;
        private readonly ISearchService _searchService;
        private readonly IMessagingService _messagingService;
        private readonly ITokenService _tokenService;
        private readonly IPolicyService _policyService;
        private readonly IKeyConnectorService _keyConnectorService;
        private readonly Action<bool> _lockedCallback;
        private readonly Func<Tuple<bool, string>, Task> _loggedOutCallback;

        public VaultTimeoutService(
            ICryptoService cryptoService,
            IStateService stateService,
            IPlatformUtilsService platformUtilsService,
            IFolderService folderService,
            ICipherService cipherService,
            ICollectionService collectionService,
            ISearchService searchService,
            IMessagingService messagingService,
            ITokenService tokenService,
            IPolicyService policyService,
            IKeyConnectorService keyConnectorService,
            Action<bool> lockedCallback,
            Func<Tuple<bool, string>, Task> loggedOutCallback)
        {
            _cryptoService = cryptoService;
            _stateService = stateService;
            _platformUtilsService = platformUtilsService;
            _folderService = folderService;
            _cipherService = cipherService;
            _collectionService = collectionService;
            _searchService = searchService;
            _messagingService = messagingService;
            _tokenService = tokenService;
            _policyService = policyService;
            _keyConnectorService = keyConnectorService;
            _lockedCallback = lockedCallback;
            _loggedOutCallback = loggedOutCallback;
        }

        public async Task<bool> IsLockedAsync(string userId = null)
        {
            var hasKey = await _cryptoService.HasKeyAsync();
            if (hasKey)
            {
                var biometricSet = await IsBiometricLockSetAsync();
                if (biometricSet && _stateService.BiometricLocked)
                {
                    return true;
                }
            }
            return !hasKey;
        }

        public async Task CheckVaultTimeoutAsync()
        {
            if (_platformUtilsService.IsViewOpen())
            {
                return;
            }

            foreach (var account in _stateService.Accounts)
            {
                if (account.UserId != null && await ShouldLockAsync(account.UserId))
                {
                    await ExecuteTimeoutActionAsync(account.UserId);
                }
            }
        }

        private async Task<bool> ShouldLockAsync(string userId)
        {
            var authed = await _stateService.IsAuthenticatedAsync(new StorageOptions { UserId = userId });
            if (!authed)
            {
                return false;
            }
            if (await IsLockedAsync(userId))
            {
                return false;
            }
            var vaultTimeoutMinutes = await GetVaultTimeout(userId);
            if (vaultTimeoutMinutes < 0 || vaultTimeoutMinutes == null)
            {
                return false;
            }
            var lastActiveTime = await _stateService.GetLastActiveTimeAsync(new StorageOptions { UserId = userId });
            if (lastActiveTime == null)
            {
                return false;
            }
            var diffMs = _platformUtilsService.GetActiveTime() - lastActiveTime;
            var vaultTimeoutMs = vaultTimeoutMinutes * 60000;
            return diffMs >= vaultTimeoutMs;
        }

        private async Task ExecuteTimeoutActionAsync(string userId)
        {
            var action = await _stateService.GetVaultTimeoutActionAsync(new StorageOptions { UserId = userId });
            if (action == "logOut")
            {
                await LogOutAsync(userId);
            }
            else
            {
                await LockAsync(true, false, userId);
            }
        }

        public async Task LockAsync(bool allowSoftLock = false, bool userInitiated = false, string userId = null)
        {
            var authed = await _stateService.IsAuthenticatedAsync(new StorageOptions { UserId = userId });
            if (!authed)
            {
                return;
            }

            if (await _keyConnectorService.GetUsesKeyConnector()) {
                var pinSet = await IsPinLockSetAsync();
                var pinLock = (pinSet.Item1 && _stateService.GetPinProtectedAsync() != null) || pinSet.Item2;

                if (!pinLock && !await IsBiometricLockSetAsync())
                {
                    await LogOutAsync();
                    return;
                }
            }

            if (allowSoftLock)
            {
                var biometricLocked = await IsBiometricLockSetAsync();
                _stateService.BiometricLocked = biometricLocked;
                if (biometricLocked)
                {
                    _messagingService.Send("locked", userInitiated);
                    _lockedCallback?.Invoke(userInitiated);
                    return;
                }
            }
            
            if (userId == null || userId == await _stateService.GetActiveUserIdAsync())
            {
                _searchService.ClearIndex();
            }
            
            await Task.WhenAll(
                _cryptoService.ClearKeyAsync(userId),
                _cryptoService.ClearOrgKeysAsync(true, userId),
                _cryptoService.ClearKeyPairAsync(true, userId),
                _cryptoService.ClearEncKeyAsync(true, userId));

            _folderService.ClearCache();
            await _cipherService.ClearCacheAsync();
            _collectionService.ClearCache();
            _searchService.ClearIndex();
            _messagingService.Send("locked", userInitiated);
            _lockedCallback?.Invoke(userInitiated);
        }
        
        public async Task LogOutAsync(string userId = null)
        {
            if(_loggedOutCallback != null)
            {
                await _loggedOutCallback.Invoke(new Tuple<bool, string>(false, userId));
            }
        }

        public async Task SetVaultTimeoutOptionsAsync(int? timeout, string action)
        {
            await _stateService.SetVaultTimeoutAsync(timeout);
            await _stateService.SetVaultTimeoutActionAsync(action);
            await _cryptoService.ToggleKeyAsync();
            await _tokenService.ToggleTokensAsync();
        }

        public async Task<Tuple<bool, bool>> IsPinLockSetAsync()
        {
            var protectedPin = await _stateService.GetProtectedPinAsync();
            var pinProtectedKey = await _stateService.GetPinProtectedAsync();
            return new Tuple<bool, bool>(protectedPin != null, pinProtectedKey != null);
        }

        public async Task<bool> IsBiometricLockSetAsync()
        {
            var biometricLock = await _stateService.GetBiometricUnlockAsync();
            return biometricLock.GetValueOrDefault();
        }

        public async Task ClearAsync(string userId = null)
        {
            await _stateService.SetPinProtectedAsync(null, new StorageOptions { UserId = userId });
            await _stateService.SetProtectedPinAsync(null, new StorageOptions { UserId = userId });
        }

        public async Task<int?> GetVaultTimeout(string userId = null) {
            var vaultTimeout = await _stateService.GetVaultTimeoutAsync();

            if (await _policyService.PolicyAppliesToUser(PolicyType.MaximumVaultTimeout)) {
                var policy = (await _policyService.GetAll(PolicyType.MaximumVaultTimeout)).First();
                // Remove negative values, and ensure it's smaller than maximum allowed value according to policy
                var policyTimeout = _policyService.GetPolicyInt(policy, "minutes");
                if (!policyTimeout.HasValue)
                {
                    return vaultTimeout;
                }

                var timeout = vaultTimeout.HasValue ? Math.Min(vaultTimeout.Value, policyTimeout.Value) : policyTimeout.Value;

                if (timeout < 0) {
                    timeout = policyTimeout.Value;
                }

                // We really shouldn't need to set the value here, but multiple services relies on this value being correct.
                if (vaultTimeout != timeout) {
                    await _stateService.SetVaultTimeoutAsync(timeout);
                }

                return timeout;
            }

            return vaultTimeout;
        }
    }
}

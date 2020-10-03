using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowUp.Common.Enums;
using WowUp.Common.Exceptions;
using WowUp.Common.Models;
using WowUp.Common.Models.Addons;
using WowUp.WPF.AddonProviders.Contracts;
using WowUp.WPF.Entities;
using WowUp.WPF.Models.WowUp;
using WowUp.WPF.Utilities;
using OctokitRateLimitExceededException = Octokit.RateLimitExceededException;
using WowUpRateLimitExceededException = WowUp.Common.Exceptions.RateLimitExceededException;

namespace WowUp.WPF.AddonProviders
{
    public class GitHubAddonProvider : IGitHubAddonProvider
    {
        private const string ProductName = "WowUp-Client";

        private static readonly string[] ReleaseContentTypes = new[] { "application/x-zip-compressed", "application/zip" };

        public string Name => "GitHub";

        public Task Scan(
            WowClientType clientType,
            AddonChannelType addonChannelType, 
            IEnumerable<AddonFolder> addonFolder)
        {
            return Task.CompletedTask;
        }

        public async Task<IList<AddonSearchResult>> GetAll(WowClientType clientType, IEnumerable<string> addonIds)
        {
            var client = GetClient();
            var searchResults = new List<AddonSearchResult>();

            foreach (var addonId in addonIds)
            {
                var result = await GetById(client, addonId, clientType);
                if (result == null)
                {
                    continue;
                }

                searchResults.Add(result);
            }

            return searchResults;
        }

        public Task<AddonSearchResult> GetById(string addonId, WowClientType clientType)
        {
            return GetById(GetClient(), addonId, clientType);
        }

        public void OnPostInstall(Addon addon)
        {
            throw new NotImplementedException();
        }

        public Task<IList<PotentialAddon>> GetFeaturedAddons(WowClientType clientType)
        {
            return Task.FromResult(new List<PotentialAddon>() as IList<PotentialAddon>);
        }

        public bool IsValidAddonUri(Uri addonUri)
        {
            return string.IsNullOrEmpty(addonUri.Host) == false &&
                addonUri.Host.EndsWith("github.com");
        }

        public Task<IEnumerable<PotentialAddon>> Search(string query, WowClientType clientType)
        {
            return Task.FromResult(new List<PotentialAddon>() as IEnumerable<PotentialAddon>);
        }

        public Task<IEnumerable<AddonSearchResult>> Search(string addonName, string folderName, WowClientType clientType, string nameOverride = null)
        {
            return Task.FromResult(new List<AddonSearchResult>() as IEnumerable<AddonSearchResult>);
        }

        public async Task<PotentialAddon> Search(Uri addonUri, WowClientType clientType)
        {
            var repositoryName = GetRepositoryNameFromAddonUri(addonUri);
            var latestVersion = await TryGetLatestVersion(GetClient(), repositoryName, clientType);
            if (latestVersion == null)
            {
                throw new NoReleaseFoundException();
            }

            return new PotentialAddon
            {
                Author = latestVersion.Repository.Owner.Login,
                DownloadCount = latestVersion.Asset.DownloadCount,
                ExternalId = GetAddonIdForRepository(repositoryName),
                ExternalUrl = latestVersion.Release.Url,
                Name = latestVersion.Asset.Name,
                ProviderName = Name,
                ThumbnailUrl = latestVersion.Repository.Owner.AvatarUrl
            };
        }

        private async Task<AddonSearchResult> GetById(GitHubClient client, string addonId, WowClientType clientType)
        {
            var repositoryName = GetRepositoryNameFromAddonId(addonId);
            var latestVersion = await TryGetLatestVersion(client, repositoryName, clientType);
            if (latestVersion == null)
            {
                return null;
            }

            var name = GetAddonName(addonId);
            return new AddonSearchResult
            {
                Author = latestVersion.Repository.Owner.Login,
                ExternalId = addonId,
                ExternalUrl = latestVersion.Asset.Url,
                Files = new List<AddonSearchResultFile> {
                    new AddonSearchResultFile
                    {
                        ChannelType = AddonChannelType.Stable,
                        DownloadUrl = latestVersion.Asset.BrowserDownloadUrl,
                        Folders = new List<string> { name },
                        GameVersion = string.Empty,
                        Version = latestVersion.Asset.Name,
                        ReleaseDate = latestVersion.Asset.CreatedAt.UtcDateTime
                    }
                },
                Name = name,
                ProviderName = Name,
                ThumbnailUrl = latestVersion.Repository.Owner.AvatarUrl
            };
        }

        private async Task<Version> TryGetLatestVersion(GitHubClient client, RepositoryName repositoryName, WowClientType clientType)
        {
            try
            {
                var results = await client.Repository.Release.GetAll(repositoryName.Owner, repositoryName.Name);
                var latestRelease = GetLatestRelease(results);
                if (latestRelease == null)
                {
                    return null;
                }

                var asset = GetValidAsset(latestRelease, clientType);
                if (asset == null)
                {
                    return null;
                }

                var repository = await client.Repository.Get(repositoryName.Owner, repositoryName.Name);
                return new Version(latestRelease, asset, repository);
            }
            catch (OctokitRateLimitExceededException ex)
            {
                throw new WowUpRateLimitExceededException(ex);
            }
        }

        private RepositoryName GetRepositoryNameFromAddonUri(Uri addonUri)
        {
            var repoPath = addonUri.LocalPath;
            var repoExtension = Path.GetExtension(repoPath);
            var repoPathParts = repoPath.Split('/');
            if (string.IsNullOrEmpty(repoPath) || !string.IsNullOrEmpty(repoExtension) || (repoPathParts.Length != 3))
            {
                throw new InvalidUrlException($"Invalid URL: {addonUri}");
            }
            return new RepositoryName(repoPathParts[1], repoPathParts[2]);
        }

        private RepositoryName GetRepositoryNameFromAddonId(string addonId)
        {
            var parts = addonId.Split('/');
            return new RepositoryName(parts[1], parts[2]);
        }

        private string GetAddonIdForRepository(RepositoryName repositoryName)
        {
            return $"/{repositoryName.Owner}/{repositoryName.Name}";
        }

        private GitHubClient GetClient()
        {
            return new GitHubClient(new ProductHeaderValue(ProductName, AppUtilities.CurrentVersionString));
        }

        private ReleaseAsset GetValidAsset(Release release, WowClientType clientType)
        {
            return release.Assets
                .Where(asset => IsNotNoLib(asset) &&
                    IsValidContentType(asset) &&
                    IsValidClientType(clientType, asset))
                .FirstOrDefault();
        }

        private Release GetLatestRelease(IEnumerable<Release> releases)
        {
            return releases
                .Where(r => !r.Draft)
                .OrderByDescending(r => r.PublishedAt)
                .FirstOrDefault();
        }

        private string GetAddonName(string addonId)
        {
            return addonId.Split("/")
                .Where(str => !string.IsNullOrEmpty(str))
                .Skip(1)
                .FirstOrDefault();
        }

        private bool IsNotNoLib(ReleaseAsset asset)
        {
            return !asset.Name.Contains("-nolib", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsValidContentType(ReleaseAsset asset)
        {
            return ReleaseContentTypes.Any(ct => ct == asset.ContentType);
        }

        private bool IsValidClientType(WowClientType clientType, ReleaseAsset asset)
        {
            var isClassic = IsClassicAsset(asset);

            switch (clientType)
            {
                case WowClientType.Retail:
                case WowClientType.RetailPtr:
                case WowClientType.Beta:
                    return !isClassic;
                case WowClientType.Classic:
                case WowClientType.ClassicPtr:
                    return isClassic;
                default:
                    return false;
            }
        }

        private bool IsClassicAsset(ReleaseAsset asset)
        {
            return asset.Name.EndsWith("-classic.zip");
        }


        private class RepositoryName
        {
            public RepositoryName(string owner, string name)
            {
                if (string.IsNullOrEmpty(owner))
                    throw new ArgumentException("Owner missing.", nameof(owner));
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("Name missing.", nameof(name));

                Owner = owner;
                Name = name;
            }

            public string Owner { get; }
            public string Name { get; }
        }


        private class Version
        {
            public Version(Release release, ReleaseAsset releaseAsset, Repository repository)
            {
                Release = release ?? throw new ArgumentNullException(nameof(release));
                ReleaseAsset = releaseAsset ?? throw new ArgumentNullException(nameof(releaseAsset));
                Repository = repository ?? throw new ArgumentNullException(nameof(repository));
            }

            public Release Release { get; }
            public ReleaseAsset ReleaseAsset { get; }
            public ReleaseAsset Asset { get; }
            public Repository Repository { get; }
        }
    }
}

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
            var addon = await GetAddon(GetClient(), repositoryName, clientType);
            if (addon == null)
            {
                throw new NoReleaseFoundException();
            }

            return new PotentialAddon
            {
                Author = addon.Repository.Owner.Login,
                DownloadCount = addon.DownloadCount,
                ExternalId = GetAddonIdForRepository(repositoryName),
                ExternalUrl = addon.Repository.HtmlUrl,
                Name = addon.Repository.Name,
                ProviderName = Name,
                ThumbnailUrl = addon.Repository.Owner.AvatarUrl
            };
        }

        private async Task<AddonSearchResult> GetById(GitHubClient client, string addonId, WowClientType clientType)
        {
            var repositoryName = GetRepositoryNameFromAddonId(addonId);
            var addon = await GetAddon(client, repositoryName, clientType);
            if (addon == null)
            {
                return null;
            }

            return new AddonSearchResult
            {
                Author = addon.Repository.Owner.Login,
                ExternalId = addonId,
                ExternalUrl = addon.Repository.HtmlUrl,
                Files = addon.LatestVersions
                    .Select(v => new AddonSearchResultFile
                    {
                        ChannelType = v.ChannelType,
                        DownloadUrl = v.Asset.BrowserDownloadUrl,
                        Folders = new List<string> { addon.Repository.Name },
                        GameVersion = string.Empty,
                        Version = v.Release.TagName,
                        ReleaseDate = v.Asset.CreatedAt.UtcDateTime
                    })
                    .ToList(),
                Name = addon.Repository.Name,
                ProviderName = Name,
                ThumbnailUrl = addon.Repository.Owner.AvatarUrl
            };
        }

        private async Task<GitHubAddon> GetAddon(GitHubClient client, RepositoryName repositoryName, WowClientType clientType)
        {
            try
            {
                var repository = await client.Repository.Get(repositoryName.Owner, repositoryName.Name);
                var releases = await client.Repository.Release.GetAll(repositoryName.Owner, repositoryName.Name);
                var latestVersions = GetLatestReleases(releases)
                    .Select(x => new { ChannelType = x.Key, Release = x.Value, Asset = GetValidAsset(x.Value, clientType) })
                    .Where(x => x.Asset != null)
                    .Select(x => new Version(x.Release, x.Asset, x.ChannelType))
                    .ToList();
                if (!latestVersions.Any())
                    return null;
                var downloadCount = GetDownloadCount(releases);
                return new GitHubAddon(repository, downloadCount, latestVersions);
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

        private Dictionary<AddonChannelType, Release> GetLatestReleases(IEnumerable<Release> releases)
        {
            return releases
                .Where(r => !r.Draft)
                .GroupBy(r => GetChannelType(r.TagName))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.PublishedAt).First());
        }

        private int GetDownloadCount(IEnumerable<Release> releases)
        {
            return releases.SelectMany(r => r.Assets).Where(IsValidContentType).Sum(a => a.DownloadCount);
        }

        private AddonChannelType GetChannelType(string tagName)
        {
            if (tagName.Contains("alpha", StringComparison.OrdinalIgnoreCase))
                return AddonChannelType.Alpha;
            if (tagName.Contains("beta", StringComparison.OrdinalIgnoreCase))
                return AddonChannelType.Beta;
            return AddonChannelType.Stable;
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


        private class GitHubAddon
        {
            public GitHubAddon(Repository repository, int downloadCount, IEnumerable<Version> latestVersions)
            {
                Repository = repository ?? throw new ArgumentNullException(nameof(repository));
                DownloadCount = downloadCount;
                LatestVersions = latestVersions.ToList();
            }

            public Repository Repository { get; }
            public int DownloadCount { get; }
            public IReadOnlyCollection<Version> LatestVersions { get; }
        }


        private class Version
        {
            public Version(Release release, ReleaseAsset asset, AddonChannelType channelType)
            {
                Release = release ?? throw new ArgumentNullException(nameof(release));
                Asset = asset ?? throw new ArgumentNullException(nameof(asset));
                ChannelType = channelType;
            }

            public Release Release { get; }
            public ReleaseAsset Asset { get; }
            public AddonChannelType ChannelType { get; }
        }
    }
}

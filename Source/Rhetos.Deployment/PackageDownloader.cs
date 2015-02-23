﻿/*
    Copyright (C) 2014 Omega software d.o.o.

    This file is part of Rhetos.

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as
    published by the Free Software Foundation, either version 3 of the
    License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using Ionic.Zip;
using NuGet;
using Rhetos.Logging;
using Rhetos.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Rhetos.Deployment
{
    /// <summary>
    /// Downloads and unpacks Rhetos packages, if not already downloaded or unpacked.
    /// Downloads dependent packages, where declare in the package's metadata file.
    /// </summary>
    public class PackageDownloader
    {
        private readonly DeploymentConfiguration _deploymentConfiguration;
        private readonly ILogProvider _logProvider;
        private readonly Rhetos.Logging.ILogger _logger;
        private readonly Rhetos.Logging.ILogger _packagesLogger;
        private readonly Rhetos.Logging.ILogger _performanceLogger;

        public PackageDownloader(
            DeploymentConfiguration deploymentConfiguration,
            ILogProvider logProvider)
        {
            _deploymentConfiguration = deploymentConfiguration;
            _logProvider = logProvider;
            _logger = logProvider.GetLogger(GetType().Name);
            _packagesLogger = logProvider.GetLogger("Packages");
            _performanceLogger = logProvider.GetLogger("Performance");
        }

        /// <summary>
        /// Downloads the packages from the provided sources, if not already downloaded.
        /// Unpacks the packages, if not already unpacked.
        /// </summary>
        public List<InstalledPackage> GetPackages()
        {
            var sw = Stopwatch.StartNew();
            var installedPackages = new List<InstalledPackage>();

            // Rhetos framework is reposted as a package for easies dependency handling. If any package depends on a specific Rhetos vesion, the version will be checked in CheckAlreadyDownloaded.
            string rhetosPackageFolder = Path.Combine(Paths.PackagesFolder, "Rhetos");
            FilesUtility.EmptyDirectory(rhetosPackageFolder);
            installedPackages.Add(new InstalledPackage("Rhetos", GetType().Assembly.GetName().Version.ToString(), new List<PackageRequest> { }, rhetosPackageFolder,
                new PackageRequest { Id = "Rhetos", RequestedBy = "Rhetos framework", Source = null, VersionsRange = null }));

            var binFileSyncer = new FileSyncer(_logProvider);
            binFileSyncer.AddDestinations(Paths.PluginsFolder, Paths.ResourcesFolder); // Even if there are no packages, those folders must be created and emptied.

            FilesUtility.SafeCreateDirectory(Paths.PackagesFolder);
            var packageRequests = _deploymentConfiguration.PackageRequests;
            while (packageRequests.Count() > 0)
            {
                var newDependencies = new List<PackageRequest>();
                foreach (var request in packageRequests)
                {
                    if (!CheckAlreadyDownloaded(request, installedPackages))
                    {
                        var installedPackage = GetPackage(request, binFileSyncer);
                        installedPackages.Add(installedPackage);
                        newDependencies.AddRange(installedPackage.Dependencies);
                    }
                }
                packageRequests = newDependencies;
            }

            DeleteObsoletePackages(installedPackages);

            binFileSyncer.UpdateDestination();

            foreach (var package in installedPackages)
                _packagesLogger.Trace(() => package.Report());
            _performanceLogger.Write(sw, "PackageDownloader.GetPackages.");
            return installedPackages;
        }

        private bool CheckAlreadyDownloaded(PackageRequest request, List<InstalledPackage> installedPackages)
        {
            var existing = installedPackages.FirstOrDefault(op => op.Id == request.Id);
            if (existing == null)
                return false;

            var requestVersionsRange = VersionUtility.ParseVersionSpec(request.VersionsRange);
            var existingVersion = SemanticVersion.Parse(existing.Version);
            if (requestVersionsRange.Satisfies(existingVersion))
                return true;

            throw new UserException(string.Format(
                "Incompatible package version '{0}, version {1}, requested by {2}' conflicts with prevously downloaded package '{3}, version {4}, requested by {5} ({6})'."
                + " Try specifying newer version of the package at the beginning of {7}.",
                request.Id, request.VersionsRange ?? "not specified", request.RequestedBy,
                existing.Id, existing.Version, existing.Request.RequestedBy, existing.Request.VersionsRange,
                DeploymentConfiguration.PackagesConfigurationFileName));
        }

        private InstalledPackage GetPackage(PackageRequest request, FileSyncer binFileSyncer)
        {
            var packageSources = SelectPackageSources(request);
            foreach (var source in packageSources)
            {
                var installedPackage = TryGetPackage(source, request, binFileSyncer);
                if (installedPackage != null)
                    return installedPackage;
            }

            throw new UserException("Cannot download package " + request.ReportIdVersionsRange()
                + ". Looked at " + packageSources.Count() + " sources:"
                + string.Concat(packageSources.Select(source => "\r\n" + source.ProcessedLocation)));
        }

        private IEnumerable<PackageSource> SelectPackageSources(PackageRequest request)
        {
            if (request.Source != null)
                return new List<PackageSource> { new PackageSource(request.Source) };
            else
                return _deploymentConfiguration.PackageSources;
        }

        private InstalledPackage TryGetPackage(PackageSource source, PackageRequest request, FileSyncer binFileSyncer)
        {
            return TryGetPackageFromUnpackedSourceFolder(source, request, binFileSyncer)
                ?? TryGetPackageFromLegacyZipPackage(source, request, binFileSyncer)
                ?? TryGetPackageFromNuGet(source, request, binFileSyncer);
        }

        //================================================================
        #region Getting the package from unpacked source folder

        private InstalledPackage TryGetPackageFromUnpackedSourceFolder(PackageSource source, PackageRequest request, FileSyncer binFileSyncer)
        {
            if (request.Source == null) // Unpacked source folder must be explicitly set in the package request.
                return null;
            if (source.Path == null || !Directory.Exists(source.Path))
                return null;

            foreach (string packedExtension in new[] { "zip", "nupkg" })
                if (Directory.GetFiles(source.Path, "*." + packedExtension).Length > 0)
                {
                    _logger.Trace(() => "Package " + request.Id + " source folder is not considered as unpacked source because it contains ." + packedExtension + " files.");
                    return null;
                }

            var metadataFiles = Directory.GetFiles(source.Path, "*.nuspec");
            if (metadataFiles.Length > 1)
            {
                _logger.Info(() => "Package " + request.Id + " source folder '" + source.ProvidedLocation + "' contains multiple .nuspec metadata files.");
                return null;
            }
            if (metadataFiles.Length == 1)
            {
                _logger.Trace(() => "Reading package " + request.Id + " from unpacked source folder with metadata " + Path.GetFileName(metadataFiles.Single()) + ".");
                return UseFilesFromUnpackedSourceWithMetadata(metadataFiles.Single(), request, binFileSyncer);
            }

            var rhetosPackageSubfolders = new[] { "DslScripts", "DataMigration", "Plugins", "Resources" };
            if (rhetosPackageSubfolders.Any(subfolder => Directory.Exists(Path.Combine(source.Path, subfolder))))
            {
                _logger.Trace(() => "Reading package " + request.Id + " from unpacked source folder without metadata file.");
                return UseFilesFromUnpackedSourceWithoutMetadata(source.Path, request, binFileSyncer);
            }

            return null;
        }

        private InstalledPackage UseFilesFromUnpackedSourceWithMetadata(string metadataFile, PackageRequest request, FileSyncer binFileSyncer)
        {
            var properties = new SimplePropertyProvider
            {
                { "Configuration", "Debug" },
            };
            var packageBuilder = new PackageBuilder(metadataFile, properties, includeEmptyDirectories: false);

            if (request.VersionsRange != null)
                if (!VersionUtility.ParseVersionSpec(request.VersionsRange).Satisfies(packageBuilder.Version))
                    throw new UserException(string.Format(
                        "Incompatible package version '{0}, version {1}'. Version {2} is requested from {3}'.",
                        packageBuilder.Id, packageBuilder.Version,
                        request.VersionsRange, request.RequestedBy));

            var sourceFolder = Path.GetDirectoryName(metadataFile);
            var targetFolder = GetTargetFolder(packageBuilder.Id, packageBuilder.Version.ToString());
            foreach (PhysicalPackageFile file in packageBuilder.Files)
                if (file.Path.StartsWith(@"Plugins\"))
                    binFileSyncer.AddFile(file.SourcePath, Paths.PluginsFolder);
                else if (file.Path.StartsWith(@"Resources\"))
                    binFileSyncer.AddFile(file.SourcePath, Paths.ResourcesFolder, Path.Combine(packageBuilder.Id, file.Path.Substring(@"Resources\".Length)));

            return new InstalledPackage(packageBuilder.Id, packageBuilder.Version.ToString(), GetNuGetPackageDependencies(packageBuilder), sourceFolder, request);
        }

        private List<PackageRequest> GetNuGetPackageDependencies(IPackageMetadata package)
        {
            return package.GetCompatiblePackageDependencies(VersionUtility.DefaultTargetFramework)
                .Select(dependency => new PackageRequest
                {
                    Id = dependency.Id,
                    VersionsRange = dependency.VersionSpec.ToString(),
                    RequestedBy = "package " + package.Id
                }).ToList();
        }

        private class SimplePropertyProvider : Dictionary<string, string>, IPropertyProvider
        {
            public dynamic GetPropertyValue(string propertyName)
            {
                string value;
                if (TryGetValue(propertyName, out value))
                    return value;
                return null;
            }
        }

        private InstalledPackage UseFilesFromUnpackedSourceWithoutMetadata(string sourceFolder, PackageRequest request, FileSyncer binFileSyncer)
        {
            string sourcePluginsFolder = Path.Combine(sourceFolder, "Plugins");
            if (Directory.Exists(sourcePluginsFolder)
                && Directory.EnumerateFiles(sourcePluginsFolder, "*.dll", SearchOption.AllDirectories).FirstOrDefault() != null
                && Directory.EnumerateFiles(sourcePluginsFolder, "*.dll", SearchOption.TopDirectoryOnly).FirstOrDefault() == null)
                _logger.Error(() => "Package " + request.Id + " source folder contains Plugins that will not be installed to the Rhetos server. Add '" + request.Id + ".nuspec' file to define the which plugin files should be installed.");

            binFileSyncer.AddFolderContent(Path.Combine(sourceFolder, "Plugins"), Paths.PluginsFolder, recursive: false);
            binFileSyncer.AddFolderContent(Path.Combine(sourceFolder, "Resources"), Paths.ResourcesFolder, request.Id, recursive: true);

            string defaultVersion = CreateVersionInRangeOrZero(request);
            return new InstalledPackage(request.Id, defaultVersion, new List<PackageRequest> { }, sourceFolder, request);
        }

        #endregion
        //================================================================
        #region Getting the package from legacy zip file

        private InstalledPackage TryGetPackageFromLegacyZipPackage(PackageSource source, PackageRequest request, FileSyncer binFileSyncer)
        {
            if (source.Path == null)
                return null;
            Version simpleVersion;
            if (!Version.TryParse(request.VersionsRange, out simpleVersion))
                return null;

            string zipPackageName = request.Id + "_" + request.VersionsRange.Replace('.', '_') + ".zip";
            string zipPackagePath = Path.Combine(source.Path, zipPackageName);

            if (!File.Exists(zipPackagePath))
            {
                _logger.Trace(() => "There is no legacy file " + zipPackageName + " in " + source.ProvidedLocation + ".");
                return null;
            }

            _logger.Trace(() => "Reading package " + request.Id + " from legacy file " + zipPackageName + ".");

            string targetFolder = GetTargetFolder(request.Id, request.VersionsRange);
            FilesUtility.EmptyDirectory(targetFolder);
            using (var zipFile = ZipFile.Read(zipPackagePath))
                foreach (var zipEntry in zipFile)
                    zipEntry.Extract(targetFolder, ExtractExistingFileAction.OverwriteSilently);

            binFileSyncer.AddFolderContent(Path.Combine(targetFolder, "Plugins"), Paths.PluginsFolder, recursive: false);
            binFileSyncer.AddFolderContent(Path.Combine(targetFolder, "Resources"), Paths.ResourcesFolder, request.Id, recursive: true);

            return new InstalledPackage(request.Id, request.VersionsRange, new List<PackageRequest> { }, targetFolder, request);
        }

        #endregion
        //================================================================
        #region Getting the package from NuGet

        private InstalledPackage TryGetPackageFromNuGet(PackageSource source, PackageRequest request, FileSyncer binFileSyncer)
        {
            var sw = Stopwatch.StartNew();

            var nugetRepository = (source.Path != null && IsLocalPath(source.Path))
                ? new LocalPackageRepository(source.Path, enableCaching: false) // When developer rebuilds a package, the package version does not need to be increased every time.
                : PackageRepositoryFactory.Default.CreateRepository(source.ProcessedLocation);
            var requestVersionsRange = !string.IsNullOrEmpty(request.VersionsRange)
                ? VersionUtility.ParseVersionSpec(request.VersionsRange)
                : new VersionSpec();
            var package = nugetRepository.FindPackage(request.Id, requestVersionsRange, allowPrereleaseVersions: true, allowUnlisted: true);
            _performanceLogger.Write(sw, () => "PackageDownloader find NuGet package " + request.Id + ".");

            if (package == null)
            {
                _logger.Trace("Package " + request.ReportIdVersionsRange() + " not found by NuGet at " + source.ProcessedLocation + ".");
                return null;
            }
            else
                _logger.Trace("Downloading NuGet package " + package.Id + " " + package.Version + " from " + source.ProcessedLocation + ".");

            var packageManager = new PackageManager(nugetRepository, Paths.PackagesFolder) {
                Logger = new LoggerForNuget(_logProvider) };
            packageManager.LocalRepository.PackageSaveMode = PackageSaveModes.Nuspec;

            packageManager.InstallPackage(package, ignoreDependencies: true, allowPrereleaseVersions: true);
            _performanceLogger.Write(sw, () => "PackageDownloader install NuGet package " + request.Id + ".");

            string targetFolder = packageManager.PathResolver.GetInstallPath(package);
            binFileSyncer.AddFolderContent(Path.Combine(targetFolder, "Plugins"), Paths.PluginsFolder, recursive: false);
            binFileSyncer.AddFolderContent(Path.Combine(targetFolder, "Resources"), Paths.ResourcesFolder, package.Id, recursive: true);

            return new InstalledPackage(package.Id, package.Version.ToString(), GetNuGetPackageDependencies(package), targetFolder, request);
        }

        private static bool IsLocalPath(string path)
        {
            var driveRegex = new Regex(@"^[a-z]\:\\$", RegexOptions.IgnoreCase);
            return driveRegex.IsMatch(Path.GetPathRoot(path));
        }

        #endregion
        //================================================================

        private static string CreateVersionInRangeOrZero(PackageRequest request)
        {
            if (request.VersionsRange != null)
            {
                var versionSpec = VersionUtility.ParseVersionSpec(request.VersionsRange);
                if (versionSpec.MinVersion != null)
                    if (versionSpec.IsMinInclusive)
                        return versionSpec.MinVersion.ToString();
                    else
                    {
                        var v = versionSpec.MinVersion.Version;
                        return new Version(v.Major, v.Minor, v.Build + 1).ToString();
                    }
            }
            return "0.0";
        }

        private static HashSet<char> _invalidPackageChars = new HashSet<char>(Path.GetInvalidFileNameChars().Concat(new[] { ' ' }));

        private static string GetTargetFolder(string packageId, string packageVersion)
        {
            char invalidChar = packageId.FirstOrDefault(c => _invalidPackageChars.Contains(c));
            if (invalidChar != default(char))
                throw new UserException("Invalid character '" + invalidChar + "' in package id '" + packageId + "'.");

            invalidChar = packageVersion.FirstOrDefault(c => _invalidPackageChars.Contains(c));
            if (invalidChar != default(char))
                throw new UserException("Invalid character '" + invalidChar + "' in package version. Package " + packageId + ", version '" + packageVersion + "'.");

            return Path.Combine(Paths.PackagesFolder, packageId + "." + packageVersion);
        }

        private void DeleteObsoletePackages(List<InstalledPackage> installedPackages)
        {
            var obsoletePackages = Directory.GetDirectories(Paths.PackagesFolder)
                .Except(installedPackages.Select(p => p.Folder));

            foreach (var folder in obsoletePackages)
                FilesUtility.SafeDeleteDirectory(folder);
        }
    }
}
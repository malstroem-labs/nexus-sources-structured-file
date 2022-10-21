using Microsoft.Extensions.Logging;
using Nexus.DataModel;
using Nexus.Extensibility;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nexus.Sources
{
    /// <summary>
    /// A base class to simplify reading data from structured, file-based data sources.
    /// </summary>
    public abstract class StructuredFileDataSource : IDataSource
    {
        // This implementation assumes the following:
        //
        // (1) The top-most folders carry rough date/time information while deeper nested
        // folders carry more fine-grained date/time information. Examples:
        //
        // OK:      /2019/2019-12/2019-12-31_12-00-00.dat
        // OK:      /2019-12/2019-12-31_12-00-00.dat
        // OK:      /2019-12/2019-12-31/2019-12-31_12-00-00.dat
        // OK:      /2019-12/2019-12-31/12-00-00.dat
        //
        // NOT OK:  /2019/12/...
        // NOT OK:  /2019/12-31/...
        // NOT OK:  /2019-12/31/...
        // NOT OK:  /2019-12-31/31/...
        //
        // NOTE: The format of the date/time is only illustrative and is being determined
        // by the specified format provider.
        //
        // (2) The files are always located in the most nested folder and not distributed
        // over the hierarchy.
        //
        // (3) Most nested folders are not empty.
        //
        // (4) File periods are constant (except for partially written files). The current
        // implementation recognizes the first of two or more partially written files within
        // a file period but ignores the rest.
        //
        // (5) UTC offset is a correction factor that should be selected so that the parsed
        // date/time of a file points to the UTC date/time of the very first representation within
        // that file.
        //
        // (6) Only file URLs are supported

        #region 

        #endregion

        #region Properties

        /// <summary>
        /// Gets the root path of the database.
        /// </summary>
        protected string Root { get; private set; } = default!;

        /// <summary>
        /// Gets the data source context. This property is not accessible from within class constructors as it will bet set later.
        /// </summary>
        protected DataSourceContext Context { get; private set; } = default!;

        /// <summary>
        /// Gets the data logger. This property is not accessible from within class constructors as it will bet set later.
        /// </summary>
        protected ILogger Logger { get; private set; } = default!;

        private Func<string, Dictionary<string, FileSource>> FileSourceProvider { get; set; } = default!;

        #endregion

        #region Protected API as seen by subclass

        /// <summary>
        /// Invoked by Nexus right after construction.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The task.</returns>
        protected virtual Task
            InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets the file source provider that provides information about the file structure within the database.
        /// </summary>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The task.</returns>
        protected abstract Task<Func<string, Dictionary<string, FileSource>>>
            GetFileSourceProviderAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Gets the catalog registrations that are located under <paramref name="path"/>.
        /// </summary>
        /// <param name="path">The parent path for which to return catalog registrations.</param>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The catalog identifiers task.</returns>
        protected abstract Task<CatalogRegistration[]>
           GetCatalogRegistrationsAsync(string path, CancellationToken cancellationToken);

        // GetCatalogAsync:
        // It is not uncommon to have measurement data files with varying channel list over
        // time. This may be caused by updated logger configurations, etc. To support these
        // scenarios, it is easy use an own mechanism. The simplest solution for a data source
        // implementation would be to maintain a list of files that contains one entry per 
        // file version, e.g. [".../fileV1.dat", ".../fileV2.dat" ] or
        // [".../2020-01-01.dat", ".../2020-06-01.dat" ].

        /// <summary>
        /// Gets the requested <see cref="ResourceCatalog"/>.
        /// </summary>
        /// <param name="catalogId">The catalog identifier.</param>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The catalog request task.</returns>
        protected abstract Task<ResourceCatalog>
            GetCatalogAsync(string catalogId, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the time range of the <see cref="ResourceCatalog"/>.
        /// </summary>
        /// <param name="catalogId">The catalog identifier.</param>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The time range task.</returns>
        protected virtual Task<(DateTime Begin, DateTime End)> 
            GetTimeRangeAsync(string catalogId, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var minDateTime = DateTime.MaxValue;
                var maxDateTime = DateTime.MinValue;

                if (Directory.Exists(Root))
                {
                    foreach (var (key, fileSource) in FileSourceProvider(catalogId))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        using var scope = Logger.BeginScope(fileSource);
                        Logger.LogDebug("Analyzing file source");

                        // first
                        var firstDateTime = StructuredFileDataSource
                            .GetCandidateFiles(Root, DateTime.MinValue, DateTime.MinValue, fileSource, cancellationToken)
                            .Select(file => file.DateTime)
                            .OrderBy(current => current)
                            .FirstOrDefault();

                        if (firstDateTime == default)
                            firstDateTime = DateTime.MaxValue;

                        firstDateTime = AdjustToUtc(firstDateTime, fileSource.UtcOffset);

                        if (firstDateTime < minDateTime)
                            minDateTime = firstDateTime;

                        // last
                        var lastDateTime = StructuredFileDataSource
                            .GetCandidateFiles(Root, DateTime.MaxValue, DateTime.MaxValue, fileSource, cancellationToken)
                            .Select(file => file.DateTime)
                            .OrderByDescending(current => current)
                            .FirstOrDefault();

                        if (lastDateTime == default)
                            lastDateTime = DateTime.MinValue;

                        lastDateTime = AdjustToUtc(lastDateTime, fileSource.UtcOffset);
                        lastDateTime = lastDateTime.Add(fileSource.FilePeriod);

                        if (lastDateTime > maxDateTime)
                            maxDateTime = lastDateTime;

                        Logger.LogDebug("Analyzing file source resulted in begin = {FirstDateTime} and end = {LastDateTime}", firstDateTime, lastDateTime);
                    }
                }
                else
                {
                    Logger.LogDebug("Folder {Root} does not exist, return default time range", Root);
                }

                return (minDateTime, maxDateTime);
            });
        }

        /// <summary>
        /// Gets the availability of the <see cref="ResourceCatalog"/>.
        /// </summary>
        /// <param name="catalogId">The catalog identifier</param>
        /// <param name="begin">The begin of the availability period.</param>
        /// <param name="end">The end of the availability period.</param>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The availability task.</returns>
        protected virtual Task<double>
            GetAvailabilityAsync(string catalogId, DateTime begin, DateTime end, CancellationToken cancellationToken)
        {
            if (begin >= end)
                throw new ArgumentException("The start time must be before the end time.");

            EnsureUtc(begin);
            EnsureUtc(end);

            // no true async file enumeration available: https://github.com/dotnet/runtime/issues/809
            return Task.Run(async () =>
            {
                double availability;

                if (Directory.Exists(Root))
                {
                    var summedAvailability = 0.0;
                    var fileSources = FileSourceProvider(catalogId);

                    foreach (var (key, fileSource) in fileSources)
                    {
                        using var scope = Logger.BeginScope(fileSource);
                        Logger.LogDebug("Analyzing file source");

                        cancellationToken.ThrowIfCancellationRequested();

                        var localBegin = begin.Add(fileSource.UtcOffset);
                        var localEnd = end.Add(fileSource.UtcOffset);

                        var candidateFiles = StructuredFileDataSource
                            .GetCandidateFiles(Root, localBegin, localEnd, fileSource, cancellationToken);

                        var files = candidateFiles
                            .Where(current => localBegin <= current.DateTime && current.DateTime < localEnd)
                            .ToList();

                        var availabilityTasks = files.Select(file =>
                        {
                            var availabilityTask = GetFileAvailabilityAsync(file.FilePath, cancellationToken);

                            _ = availabilityTask.ContinueWith(
                                x => Logger.LogDebug(availabilityTask.Exception, "Could not process file {FilePath}", file.FilePath),
                                TaskContinuationOptions.OnlyOnFaulted
                            );

                            return availabilityTask;
                        });

                        var availabilities = await Task.WhenAll(availabilityTasks);
                        var actual = availabilities.Sum();
                        var total = (end - begin).Ticks / (double)fileSource.FilePeriod.Ticks;

                        summedAvailability += actual / total;
                    }

                    availability = summedAvailability / fileSources.Count;
                }
                else
                {
                    availability = 0.0;
                    Logger.LogDebug("Folder {Root} does not exist, return default availabilit.", Root);
                }

                return availability;
            });
        }

        /// <summary>
        /// Returns the availability within a file.
        /// </summary>
        /// <param name="filePath">The file path.</param>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>the availability within a file.</returns>
        protected virtual Task<double> 
            GetFileAvailabilityAsync(string filePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(1.0);
        }

        /// <summary>
        /// Reads a dataset.
        /// </summary>
        /// <param name="catalogItem">The catalog item to read.</param>
        /// <param name="begin">The beginning of the period to read.</param>
        /// <param name="end">The end of the period to read.</param>
        /// <param name="data">The data buffer.</param>
        /// <param name="status">The status buffer.</param>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The task.</returns>
        protected virtual async Task 
            ReadSingleAsync(CatalogItem catalogItem, DateTime begin, DateTime end, Memory<byte> data, Memory<byte> status, CancellationToken cancellationToken)
        {
            var representation = catalogItem.Representation;
            var catalog = catalogItem.Catalog;
            var samplePeriod = representation.SamplePeriod;
            var fileSourceId = catalogItem.Resource.Properties?.GetStringValue(StructureFileDataModelExtensions.FileSourceIdKey)!;
            var fileSource = FileSourceProvider(catalogItem.Catalog.Id)[fileSourceId];
            var fileLength = fileSource.FilePeriod.Ticks / samplePeriod.Ticks;
            var originalName = catalogItem.Resource.Properties?.GetStringValue(StructureFileDataModelExtensions.OriginalNameKey)!;

            var bufferOffset = 0;
            var currentBegin = begin;
            var totalPeriod = end - begin;
            var consumedPeriod = TimeSpan.Zero;
            var remainingPeriod = totalPeriod;

            while (consumedPeriod < totalPeriod)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // get file path and begin
                (var filePaths, var fileBegin) = await FindFilePathsAsync(currentBegin, fileSource);

                // determine file begin if not yet done using the first file name returned
                if (fileBegin == default)
                {
                    if (!StructuredFileDataSource.TryGetFileBeginByPath(filePaths.First(), fileSource, out fileBegin, default))
                        throw new Exception($"Unable to determine date/time of file {filePaths.First()}.");
                }

                /* CB = Current Begin, FP = File Period
                 * 
                 *  begin    CB-FP        CB         CB+FP                 end
                 *    |--------|-----------|-----------|-----------|--------|
                 */
                var CB_MINUS_FP = currentBegin - fileSource.FilePeriod;
                var CB_PLUS_FP = currentBegin + fileSource.FilePeriod;

                int fileBlock;
                TimeSpan currentPeriod;

                /* normal case: current begin may be greater than file begin if: 
                 * - this is the very first iteration
                 * - the current file begins later than expected (incomplete file)
                 */
                if (CB_MINUS_FP < fileBegin && fileBegin <= currentBegin)
                {
                    var consumedFilePeriod = currentBegin - fileBegin;
                    var remainingFilePeriod = fileSource.FilePeriod - consumedFilePeriod;

                    currentPeriod = TimeSpan.FromTicks(Math.Min(remainingFilePeriod.Ticks, remainingPeriod.Ticks));
                    Logger.LogTrace("Process period {CurrentBegin} to {CurrentEnd}", currentBegin, currentBegin + currentPeriod);

                    fileBlock = (int)(currentPeriod.Ticks / samplePeriod.Ticks);

                    var fileOffset = consumedFilePeriod.Ticks / samplePeriod.Ticks;

                    foreach (var filePath in filePaths)
                    {
                        if (File.Exists(filePath))
                        {
                            Logger.LogTrace("Process file {FilePath}", filePath);

                            try
                            {
                                var slicedData = data
                                    .Slice(bufferOffset * representation.ElementSize, fileBlock * representation.ElementSize);

                                var slicedStatus = status
                                    .Slice(bufferOffset, fileBlock);

                                var readInfo = new ReadInfo(
                                    originalName,
                                    filePath,
                                    catalogItem,
                                    fileSource,
                                    slicedData,
                                    slicedStatus,
                                    fileBegin,
                                    fileOffset,
                                    fileBlock,
                                    fileLength
                                );

                                await this
                                    .ReadSingleAsync(readInfo, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                Logger.LogDebug(ex, "Could not process file {FilePath}", filePath);
                            }
                        }
                        else
                        {
                            Logger.LogDebug("File {FilePath} does not exist", filePath);
                        }
                    }
                }
                /* there was an incomplete file, skip the incomplete part */
                else if (CB_PLUS_FP <= fileBegin && fileBegin < end)
                {
                    Logger.LogDebug("Skipping period {FileBegin} to {CurrentBegin}", fileBegin, currentBegin);
                    currentPeriod = fileBegin - currentBegin;
                    fileBlock = (int)(currentPeriod.Ticks / samplePeriod.Ticks);
                }
                else
                {
                    break;
                }

                // update loop state
                bufferOffset += fileBlock;
                currentBegin += currentPeriod;
                consumedPeriod += currentPeriod;
                remainingPeriod -= currentPeriod;
            }
        }

        /// <summary>
        /// Reads a dataset from the provided file.
        /// </summary>
        /// <param name="info">The read information.</param>
        /// <param name="cancellationToken">A token to cancel the current operation.</param>
        /// <returns>The task.</returns>
        protected abstract Task
            ReadSingleAsync(ReadInfo info, CancellationToken cancellationToken);

        /// <summary>
        /// Finds files given the date/time and the <see cref="FileSource"/>.
        /// </summary>
        /// <param name="begin">The file begin.</param>
        /// <param name="fileSource">The file source.</param>
        /// <returns>A tuple of file names and date/times.</returns>
        /// <exception cref="ArgumentException">Thrown when the begin value does not have its kind property set.</exception>
        protected virtual Task<(string[], DateTime)> 
            FindFilePathsAsync(DateTime begin, FileSource fileSource)
        {
            // This implementation assumes that the file start times are aligned to multiples
            // of the file period. Depending on the file template, it is possible to find more
            // than one matching file. There is one special case where two files are expected:
            // A data logger creates versioned files with a granularity of e.g. 1 file per day.
            // When the version changes, the logger creates a new file with same name but new
            // version. This could look like this:
            // 2020-01-01T00-00-00Z_v1.dat (contains data from midnight to time t0)
            // 2020-01-01T00-00-00Z_v2.dat (contains data from time t0 + x to next midnight)
            // Where x is the time period the system was offline to apply the new version.

            var roundedBegin = begin.RoundDown(fileSource.FilePeriod);

            var localBegin = roundedBegin.Kind switch
            {
                DateTimeKind.Local => roundedBegin,
                DateTimeKind.Utc => DateTime.SpecifyKind(roundedBegin.Add(fileSource.UtcOffset), DateTimeKind.Local),
                _ => throw new ArgumentException("The begin parameter must have its kind property specified.")
            };

            var folderNames = fileSource
                .PathSegments
                .Select(segment => localBegin.ToString(segment));

            var folderNameArray = new List<string>() { Root }
                .Concat(folderNames)
                .ToArray();

            var folderPath = Path.Combine(folderNameArray);
            var fileName = localBegin.ToString(fileSource.FileTemplate);

            string[] filePaths;

            if (fileName.Contains("?") || fileName.Contains("*") && Directory.Exists(folderPath))
            {
                filePaths = Directory
                   .EnumerateFiles(folderPath, fileName)
                   .ToArray();
            }
            else
            {
                filePaths = new string[] { Path.Combine(folderPath, fileName) };
            }

            return Task.FromResult((filePaths, roundedBegin));
        }

        /// <summary>
        /// Tries to find the first file for a given <see cref="FileSource"/>. 
        /// </summary>
        /// <param name="fileSource">The file source for which to find the first file.</param>
        /// <param name="filePath">The found file path.</param>
        /// <returns>True when a file was found, false otherwise.</returns>
        protected bool TryGetFirstFile(FileSource fileSource, [NotNullWhen(true)] out string? filePath)
        {
            filePath = StructuredFileDataSource
                .GetCandidateFiles(
                    Root,
                    DateTime.MinValue,
                    DateTime.MinValue,
                    fileSource,
                    CancellationToken.None)
                .Select(file => file.FilePath)
                .FirstOrDefault();

            return filePath is not null;
        }

        #endregion

        #region Public API as seen by Nexus and unit tests

        async Task 
            IDataSource.SetContextAsync(DataSourceContext context, ILogger logger, CancellationToken cancellationToken)
        {
            if (context.ResourceLocator is null)
                throw new Exception("The resource locator parameter is required.");

            Root = context.ResourceLocator.ToPath();
            Context = context;
            Logger = logger;

            await InitializeAsync(cancellationToken);
            FileSourceProvider = await GetFileSourceProviderAsync(cancellationToken);
        }

        Task<CatalogRegistration[]>
           IDataSource.GetCatalogRegistrationsAsync(string path, CancellationToken cancellationToken)
        {
            return GetCatalogRegistrationsAsync(path, cancellationToken);
        }

        async Task<ResourceCatalog> 
            IDataSource.GetCatalogAsync(string catalogId, CancellationToken cancellationToken)
        {
            var catalog = await GetCatalogAsync(catalogId, cancellationToken);

            if (catalog.Resources is not null)
            {
                foreach (var resource in catalog.Resources)
                {
                    // ensure file source id
                    var fileSourceId = resource.Properties?.GetStringValue(StructureFileDataModelExtensions.FileSourceIdKey);

                    if (string.IsNullOrWhiteSpace(fileSourceId))
                        throw new Exception($"The resource {resource.Id} is missing the file source property.");

                    // ensure original name
                    var originalName = resource.Properties?.GetStringValue(StructureFileDataModelExtensions.OriginalNameKey);

                    if (string.IsNullOrWhiteSpace(originalName))
                        throw new Exception($"The resource {resource.Id} is missing the original name property.");
                }
            }

            return catalog;
        }

        Task<(DateTime Begin, DateTime End)> 
            IDataSource.GetTimeRangeAsync(string catalogId, CancellationToken cancellationToken)
        {
            return GetTimeRangeAsync(catalogId, cancellationToken);
        }

        Task<double> 
            IDataSource.GetAvailabilityAsync(string catalogId, DateTime begin, DateTime end, CancellationToken cancellationToken)
        {
            return GetAvailabilityAsync(catalogId, begin, end, cancellationToken);
        }

        async Task
            IDataSource.ReadAsync(DateTime begin, DateTime end, ReadRequest[] requests, ReadDataHandler readData, IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (begin >= end)
                throw new ArgumentException("The start time must be before the end time.");

            EnsureUtc(begin);
            EnsureUtc(end);

            var counter = 0.0;

            foreach (var (catalogItem, dataBuffer, statusBuffer) in requests)
            {
                using var scope = Logger.BeginScope(new Dictionary<string, object>()
                {
                    ["ResourcePath"] = catalogItem.ToPath()
                });

                Logger.LogDebug("Read catalog item");

                try
                {
                    await ReadSingleAsync(catalogItem, begin, end, dataBuffer, statusBuffer, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Could not read catalog item");
                }

                progress.Report(++counter / requests.Length);
            }
        }

        #endregion

        #region Helpers

        private static IEnumerable<(string FilePath, DateTime DateTime)> 
            GetCandidateFiles(string rootPath, DateTime begin, DateTime end, FileSource fileSource, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // initial check
            if (!Directory.Exists(rootPath))
                return new List<(string, DateTime)>();

            // get all candidate folders
            var candidateFolders = fileSource.PathSegments.Length >= 1

                ? StructuredFileDataSource
                    .GetCandidateFolders(rootPath, default, begin, end, fileSource.PathSegments, cancellationToken)

                : new List<(string, DateTime)>() { (rootPath, default) };

            return candidateFolders.SelectMany(currentFolder =>
            {
                var filePaths = Directory.EnumerateFiles(currentFolder.FolderPath);

                var candidateFiles = filePaths
                    .Select(filePath =>
                    {
                        var success = StructuredFileDataSource
                            .TryGetFileBeginByPath(filePath, fileSource, out var fileBegin, currentFolder.DateTime);

                        return (success, filePath, fileBegin);
                    })
                    .Where(current => current.success)
                    .Select(current => (current.filePath, current.fileBegin));

                return candidateFiles;
            });
        }

        private static IEnumerable<(string FolderPath, DateTime DateTime)> 
            GetCandidateFolders(string root, DateTime rootDate, DateTime begin, DateTime end, string[] pathSegments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // get all available folders
            var folderPaths = Directory
                .EnumerateDirectories(root)
                .ToList();

            // get all folders that can be parsed
            var hasDateTimeInformation = false;

            var folderNameToDateTimeMap = folderPaths
                .Select(folderPath =>
                {
                    var folderName = Path.GetFileName(folderPath);

                    var success = DateTime
                        .TryParseExact(
                            folderName,
                            pathSegments.First(),
                            default,
                            DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AdjustToUniversal,
                            out var parsedDateTime
                        );

                    if (parsedDateTime == default)
                        parsedDateTime = rootDate;

                    else
                        hasDateTimeInformation = true;

                    return (folderPath, parsedDateTime);
                })
               .ToDictionary(current => current.folderPath, current => current.parsedDateTime);

            // keep only folders that fall within the wanted time range

            /* The expected segment name is used for two purposes:
             * (1) for "filter by search date" where only the begin date/time matters
             * (2) for "filter by exact match" where any date/time can be put into
             * the ToString() method to remove the quotation marks from the path segment
             */
            var expectedSegmentName = begin.ToString(pathSegments.First());

            var folderCandidates = hasDateTimeInformation

                // filter by search date
                ? StructuredFileDataSource
                    .FilterBySearchDate(begin, end, folderNameToDateTimeMap, expectedSegmentName)

                // filter by exact match
                : folderNameToDateTimeMap
                    .Where(entry => Path.GetFileName(entry.Key) == expectedSegmentName)
                    .Select(entry => (entry.Key, entry.Value));

            // go deeper
            if (pathSegments.Count() > 1)
            {
                return folderCandidates.SelectMany(current =>
                    StructuredFileDataSource.GetCandidateFolders(
                        current.Key,
                        current.Value,
                        begin,
                        end,
                        pathSegments.Skip(1).ToArray(), 
                        cancellationToken
                    )
                );
            }

            // we have reached the most nested folder level
            else
            {
                return folderCandidates;
            }
        }

        private static IEnumerable<(string Key, DateTime Value)> 
            FilterBySearchDate(DateTime begin, DateTime end, Dictionary<string, DateTime> folderNameToDateTimeMap, string expectedSegmentName)
        {
            if (begin == DateTime.MinValue && end == DateTime.MinValue)
            {
                var folderCandidate = folderNameToDateTimeMap
                    .OrderBy(entry => entry.Value)
                    .FirstOrDefault();

                return new List<(string, DateTime)>() { (folderCandidate.Key, folderCandidate.Value) };
            }

            else if (begin == DateTime.MaxValue && end == DateTime.MaxValue)
            {
                var folderCandidate = folderNameToDateTimeMap
                   .OrderByDescending(entry => entry.Value)
                   .FirstOrDefault();

                return new List<(string, DateTime)>() { (folderCandidate.Key, folderCandidate.Value) };
            }

            else
            {
                return folderNameToDateTimeMap
                    .Where(entry =>
                    {
                        // Check for the case that the parsed date/time
                        // (1) is more specific (2020-01-01T22) than the search time range (2020-01-01T00 - 2021-01-02T00):
                        // (2) is less specific but in-between (2020-02) the search time range (2020-01-01 - 2021-03-01)
                        if (begin <= entry.Value && entry.Value < end)
                            return true;

                        // Check for the case that the parsed date/time
                        // (1) is less specific (2020-01) and outside the search time range (2020-01-02 - 2020-01-03)
                        else
                            return Path.GetFileName(entry.Key) == expectedSegmentName;
                    })
                    .Select(entry => (entry.Key, entry.Value));
            }
        }

        private static bool 
            TryGetFileBeginByPath(string filePath, FileSource fileSource, out DateTime fileBegin, DateTime folderBegin = default)
        {
            var fileName = Path.GetFileName(filePath);

            if (StructuredFileDataSource.TryGetFileBeginByName(fileName, fileSource, out fileBegin))
            {
                // When TryGetFileBeginByName == true, then the input string was parsed successfully and the
                // result contains date/time information of either kind: date+time, time-only, default.

                // date+time: use file date/time
                if (fileBegin.Date != default)
                {
                    return true;
                }

                // time-only: use combined folder and file date/time
                else if (fileBegin != default)
                {
                    // short cut
                    if (folderBegin != default)
                    {
                        fileBegin = new DateTime(folderBegin.Date.Ticks + fileBegin.TimeOfDay.Ticks, fileBegin.Kind);
                        return true;
                    }

                    // long way
                    else
                    {
                        var pathSegments = filePath
                            .Split('/', '\\');

                        pathSegments = pathSegments
                            .Skip(pathSegments.Length - fileSource.PathSegments.Length)
                            .ToArray();

                        for (int i = 0; i < pathSegments.Length; i++)
                        {
                            var folderName = pathSegments[i];
                            var folderTemplate = fileSource.PathSegments[i];

                            var _ = DateTime.TryParseExact(
                                folderName,
                                folderTemplate,
                                default,
                                DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AdjustToUniversal,
                                out var currentFolderBegin
                            );

                            if (currentFolderBegin > folderBegin)
                                folderBegin = currentFolderBegin;
                        }

                        fileBegin = folderBegin;
                        return fileBegin != default;
                    }
                }
                // default: use folder date/time
                else
                {
                    fileBegin = folderBegin;
                    return fileBegin != default;
                }
            }
            // no date + no time: failed
            else
            {
                return false;
            }
        }

        private static bool 
            TryGetFileBeginByName(string fileName, FileSource fileSource, out DateTime fileBegin)
        {
            /* (1) Regex is required in scenarios when there are more complex
             * file names, i.e. file names containing an opaque string that
             * changes for every file. This could be a counter, a serial
             * number or some other unpredictable proprietary string.
             *
             * (2) It is also required as a filter if there is more than one
             * file type in the containing folder, e.g. high frequent and
             * averaged data files that are being treated as different sources.
             */

            var fileTemplate = fileSource.FileTemplate;

            if (!string.IsNullOrWhiteSpace(fileSource.FileDateTimePreselector))
            {
                if (string.IsNullOrEmpty(fileSource.FileDateTimeSelector))
                    throw new Exception("When a file date/time preselector is provided, the selector itself must be provided too.");

                fileTemplate = fileSource.FileDateTimeSelector;
                var regex = new Regex(fileSource.FileDateTimePreselector);

                fileName = string.Join("", regex
                    .Match(fileName)
                    .Groups
                    .Cast<Group>()
                    .Skip(1)
                    .Select(match => match.Value)
                );
            }

            var success = DateTime.TryParseExact(
                fileName,
                fileTemplate,
                default,
                DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AdjustToUniversal,
                out fileBegin
            );

            return success;
        }

        private void 
            EnsureUtc(DateTime dateTime)
        {
            if (dateTime.Kind != DateTimeKind.Utc)
                throw new ArgumentException("UTC date/times are required.");
        }

        private DateTime 
            AdjustToUtc(DateTime dateTime, TimeSpan utcOffset)
        {
            var result = dateTime;

            if (dateTime != DateTime.MinValue && dateTime != DateTime.MaxValue)
            {
                if (dateTime.Kind != DateTimeKind.Utc)
                    result = DateTime.SpecifyKind(dateTime.Subtract(utcOffset), DateTimeKind.Utc);
            }

            return result;
        }

        #endregion
    }
}

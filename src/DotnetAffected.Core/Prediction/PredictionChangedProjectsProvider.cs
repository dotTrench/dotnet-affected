using DotnetAffected.Abstractions;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Microsoft.Build.Prediction.Predictors;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;

namespace DotnetAffected.Core
{
    /// <summary>
    /// Determines which projects have changed based on the list of files that have changed.
    /// Uses MSBuild.Prediction to figure out which files are input of which projects.
    /// </summary>
    public class PredictionChangedProjectsProvider : IChangedProjectsProvider
    {
        private readonly ProjectGraph _graph;

        private static readonly ProjectFileAndImportsGraphPredictor[] GraphPredictors = new[]
        {
            new ProjectFileAndImportsGraphPredictor()
        };

        /// <summary>
        /// Keeps a list of all predictors that predict input files.
        /// When Microsoft.Build.Prediction is updated, this list needs to be reviewed.
        /// </summary>
        private static readonly IProjectPredictor[] ProjectPredictors = Microsoft.Build.Prediction.ProjectPredictors
            .AllProjectPredictors
            .Where(p => p.GetType() != typeof(OutDirOrOutputPathPredictor))
            .ToArray();

        private readonly ProjectGraphPredictionExecutor _executor = new ProjectGraphPredictionExecutor(
            GraphPredictors,
            ProjectPredictors);

        /// <summary>
        /// REMARKS: we have other means for detecting changes excluded files 
        /// </summary>
        private readonly string[] _fileExclusions = new[]
        {
            // Predictors won't take into account package references
            "Directory.Packages.props"
        };

        private readonly IDiscoveryOptions _options;

        /// <summary>
        /// Creates the <see cref="PredictionChangedProjectsProvider"/>.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="options"></param>
        public PredictionChangedProjectsProvider(
            ProjectGraph graph,
            IDiscoveryOptions options)
        {
            _graph = graph;
            _options = options;
        }

        /// <inheritdoc />
        public IEnumerable<ProjectGraphNode> GetReferencingProjects(IEnumerable<string> files)
        {
            // normalize paths so that they match on windows.
            var normalizedFiles = files
                .Where(f => !_fileExclusions.Any(f.EndsWith))
                .Select(Path.GetFullPath)
                .ToList();

            var fs = new ChangedFileFileSystem(normalizedFiles);
            var context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared, fs);
            ProjectGraph.ProjectInstanceFactoryFunc fn = (path, properties, collection) =>
                ProjectInstance.FromFile(
                    path,
                    new ProjectOptions
                    {
                        ProjectCollection = collection, GlobalProperties = properties, EvaluationContext = context
                    }
                );

            var entrypoints =
                _graph
                    .EntryPointNodes
                    .Select(it => new ProjectGraphEntryPoint(it.ProjectInstance.FullPath));

            var graph = new ProjectGraph(
                entrypoints,
                ProjectCollection.GlobalProjectCollection,
                fn,
                1,
                CancellationToken.None
            );

            var collector = new FilesByProjectGraphCollector(graph, _options.RepositoryPath);
            _executor.PredictInputsAndOutputs(graph, collector);

            var hasReturned = new HashSet<string>();
            foreach (var file in normalizedFiles)
            {
                // determine nodes depending on the changed file
                var nodesWithFiles = collector.PredictionsPerNode
                    .Where(x => x.Value.Contains(file));

                foreach (var (key, _) in nodesWithFiles)
                {
                    if (hasReturned.Add(key.ProjectInstance.FullPath))
                    {
                        yield return key;
                    }
                }
            }
        }

        private sealed class ChangedFileFileSystem : MSBuildFileSystemBase
        {
            private readonly List<string> _files;

            public ChangedFileFileSystem(List<string> files)
            {
                _files = files;
            }

            public override bool FileExists(string path)
            {
                return base.FileExists(path) || _files.Contains(path);
            }


            public override IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                if (searchOption == SearchOption.AllDirectories)
                {
                    return _files
                        .Where(it => it.StartsWith(path) && FileSystemName.MatchesWin32Expression(searchPattern, it))
                        .Concat(base.EnumerateFiles(path, searchPattern, searchOption));
                }

                return _files
                    .Where(it =>
                        Path.GetDirectoryName(it) == path && FileSystemName.MatchesWin32Expression(searchPattern, it))
                    .Concat(base.EnumerateFiles(path, searchPattern, searchOption));
            }
        }
    }
}

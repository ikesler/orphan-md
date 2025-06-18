using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = System.IO.Directory;

var dryRunFolderOption = new Option<bool?>(
    name: "--dry-run",
    description: "Search orphans but don't delete them.");

var whatFolderOption = new Option<string>(
    name: "--what",
    description: "What files to look for. Path to a directory, iterated in shallow manner.")
{
    IsRequired = true
};

var whereFolderOption = new Option<string>(
    name: "--where",
    description: "Where to look for references. Path to a directory, iterated in deep manner. May contain --what directory - it will be ignored.")
{
    IsRequired = true
};

var indexFolderOption = new Option<string?>(
    name: "--index",
    description: "A previously created index file to use. If not specified, a new one will be created.");

var rootCommand = new RootCommand("Find and delete files which are not referenced by a markdown doc.");
rootCommand.AddOption(whatFolderOption);
rootCommand.AddOption(whereFolderOption);
rootCommand.AddOption(indexFolderOption);
rootCommand.AddOption(dryRunFolderOption);

rootCommand.SetHandler((what, where, index, dryRun) =>
{
    const LuceneVersion appLuceneVersion = LuceneVersion.LUCENE_48;

    var basePath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var indexPath = index ?? Path.Combine(basePath, $"orphan-md-index-{Guid.NewGuid():N}");
    Console.WriteLine($"Index file: {indexPath}");

    using var dir = FSDirectory.Open(indexPath);

    var analyzer = new StandardAnalyzer(appLuceneVersion);

    var indexConfig = new IndexWriterConfig(appLuceneVersion, analyzer);
    if (index == null)
    {
        using var writer = new IndexWriter(dir, indexConfig);
        var mdFiles = Directory.GetFiles(where, "*.md", SearchOption.AllDirectories);
        foreach (var mdFile in mdFiles.Where(x => !x.Contains(what, StringComparison.OrdinalIgnoreCase)))
        {
            var doc = new Document()
            {
                new StringField("path", mdFile, Field.Store.YES),
                new TextField("content", File.ReadAllText(mdFile), Field.Store.YES)
            };
            writer.AddDocument(doc);
            writer.Flush(triggerMerge: false, applyAllDeletes: false);
        }
    }

    using var reader = DirectoryReader.Open(dir);
    var searcher = new IndexSearcher(reader);
    var files = Directory.GetFiles(what, "*.*", SearchOption.AllDirectories);
    var hits = 0;
    var parser = new QueryParser(appLuceneVersion, "content", analyzer)
    {
        DefaultOperator = Operator.AND
    };
    foreach (var file in files)
    {
        var term = file.Replace(what, "").Replace("\\", "/");
        var query = parser.Parse($"\"{term}\"");
        var topDocs = searcher.Search(query, 1);
        if (topDocs.TotalHits == 0)
        {
            Console.WriteLine(file);
            ++hits;
            if (!dryRun ?? false)
            {
                File.Delete(file);
            }
        }
    }

    Console.WriteLine($"Found {hits} orphans from {files.Length}.");
}, whatFolderOption, whereFolderOption, indexFolderOption, dryRunFolderOption);

var parser = new CommandLineBuilder(rootCommand)
    .UseDefaults()
    .Build();

return await parser.InvokeAsync(args);

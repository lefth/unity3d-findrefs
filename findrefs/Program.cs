using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#if !UNITY
using CommandLine;
#endif

namespace findrefs
{
	class ReferentAsset
	{
		readonly public bool m_IsScript;
		readonly public string m_Guid;
		readonly public bool m_IsAssetBundle;
		readonly public string m_Path;

		public ReferentAsset(string searchString)
		{
			bool probablyScript = searchString.EndsWith(".cs", StringComparison.InvariantCultureIgnoreCase)
				|| searchString.EndsWith(".js", StringComparison.InvariantCultureIgnoreCase)
				|| !searchString.Contains(".");

			if (probablyScript && !string.IsNullOrEmpty(Program.m_ScriptsDir))
				m_Path = FindMatchingFilename(searchString, Program.m_ScriptsDir);

			m_Path = m_Path
				?? FindMatchingFilename(searchString, Program.m_AssetsDir);
			if (m_Path == null)
				throw new NotFoundException(searchString);

			var extension = Path.GetExtension(m_Path).ToLower();
			m_IsScript = extension == ".cs" || extension == ".js";

			GetMetadata(m_Path, out m_Guid, out m_IsAssetBundle);
		}

		public ReferentAsset(ReferentAsset original)
		{
			m_Guid = original.m_Guid;
			m_IsAssetBundle = original.m_IsAssetBundle;
			m_IsScript = original.m_IsScript;
			m_Path = original.m_Path;
		}

		public static string FindMatchingFilename(string searchString, string searchDir)
		{
			string lcSearch = searchString.ToLower();
			Predicate<string> matchFunc;
			//if (searchString.Contains("."))
			//	matchFunc = (string arg) => arg.EndsWith(lcSearch);
			//else
			matchFunc = (string arg) => !arg.EndsWith(".meta") && !arg.EndsWith(".unity") && arg.Contains(lcSearch);

			Dictionary<string, string> matches = new Dictionary<string, string>();
			if (File.Exists(searchString))
			{
				// If the search string exists (as an exact path), use it as the match, rather
				// than trying to find a different close match for the term.
				matches[Path.GetFileName(searchString)] = searchString;
			}
			else
			{
				foreach (var filePath in Program.Find(searchDir))
				{
					if (matchFunc(filePath.ToLower()))
					{
						matches[Path.GetFileName(filePath)] = filePath;
						Program.PrintPath(filePath, $"\tMatches:  {Path.GetFileName(filePath)}  --  ");
					}
				}
			}

			string bestMatch = null;
			int bestScore = 9999;
			foreach (var kvp in matches)
			{
				var key = kvp.Key;
				int score = key.Length;
				if (key.StartsWith(searchString))
					score -= 4; // give it a healthy bonus
				if (score < bestScore)
				{
					bestScore = score;
					bestMatch = kvp.Value;
				}
			}

			return bestMatch;
		}

		private static void GetMetadata(string assetPath, out string guid, out bool isAssetBundle)
		{
			const string assetBundlePrefix = "  assetBundleName: ";
			const int assetBundlePrefixLength = 19;

			guid = null;
			isAssetBundle = false;

			using (var fstream = File.OpenRead($"{assetPath}.meta"))
			using (var reader = new StreamReader(fstream))
			{
				while (!reader.EndOfStream)
				{
					var line = reader.ReadLine();
					if (guid == null && line.StartsWith("guid: "))
						guid = line.Substring("guid: ".Length);
					else if (!isAssetBundle && line.StartsWith(assetBundlePrefix) && line.Length > assetBundlePrefixLength)
						isAssetBundle = true;
				}
			}

			if (guid == null)
				throw new Exception($"GUID not found for .meta for asset: {assetPath}");
		}

		public bool Matches(ReferentAsset referent)
		{
			return referent.m_Path == m_Path;
		}
	}
	class ReferentAssetWithBasename : ReferentAsset
	{
		public readonly string m_Basename;
		public ReferentAssetWithBasename(ReferentAsset asset, string basename) : base(asset)
		{
			m_Basename = basename;
		}
	}

#if !UNITY
	class Options
	{
		[Option("binary", HelpText = "Search for resources in binary files as well")]
		public bool searchBinaryAlso { get; set; }
		[Option("absolute-paths")]
		public bool _absolute { set { absolute = value; } }
		[Option("absolute")]
		public bool absolute { get; set; }
		//[Option("print-referrers")]
		[Option("verbose", HelpText = "Print which file refers to which referent.")]
		public bool printReferrers { get; set; }
		[Option("first-reference-only", HelpText = "Optimization: print only the first reference.")]
		public bool firstReferenceOnly { get; set; }
		[Option("print-unreferenced")]
		public bool printUnreferenced { get; set; }
		[Option("unreferenced")]
		public bool _printUnreferenced
		{
			get { return printUnreferenced; }
			set { printUnreferenced = value; }
		}
		[Option("as-resources-only", Default = false)]
		public bool asResourcesOnly { get; set; }
		[Value(0, MetaName = "File name(s)", HelpText = "Files (or file name fragments) to find Unity refenences to", Required = true)]
		public IEnumerable<string> input { get; set; }
		[Option("debug_search-in-these-files-only", Separator = ',')]
		public IList<string> searchInLimitedFiles { get; set; }
	}
#endif

	class Program
	{
		private static object m_WriteLock = new object();
		private static bool m_RelativeOutput;
		private static bool m_PrintUnreferenced;
		private static bool m_PrintReferrers;
		private static bool m_FirstReferenceOnly;
		private static bool m_AsResourcesOnly;
		private static readonly int m_MaxConcurrency = Environment.ProcessorCount - 1;

		public static string m_AssetsDir { get; private set; }
		// Optimization: look for .cs/.js files in one directory first
		public static string m_ScriptsDir { get; private set; }
		public static bool m_SearchBinaries { get; private set; }
		public static IList<string> m_LimitedFilesToSearch { get; private set; }

#if !UNITY
		static void Main(string[] args)
		{
			IEnumerable<string> _searchStrings = null;
			Parser.Default.ParseArguments<Options>(args)
				.WithParsed(o =>
				{
					m_RelativeOutput = !o.absolute;
					m_PrintUnreferenced = o.printUnreferenced;
					m_PrintReferrers = o.printReferrers;
					m_FirstReferenceOnly = o.firstReferenceOnly;
					m_AsResourcesOnly = o.asResourcesOnly;
					m_SearchBinaries = o.searchBinaryAlso;
					_searchStrings = o.input;
					if (o.searchInLimitedFiles != null && o.searchInLimitedFiles.Any())
						m_LimitedFilesToSearch = o.searchInLimitedFiles;
				})
				//.WithNotParsed(o => { Console.WriteLine(m_RelativeOutput); }) // XXX
				//.WithParsed   (o => { Console.WriteLine(m_RelativeOutput); }) // XXX
				.WithNotParsed(errors =>
				{
					foreach (var error in errors)
						Console.WriteLine(error);
					Environment.Exit(1);
				});

			FindReferencesAsync(_searchStrings).Wait();
		}
#endif

		public static void Init()
		{
			var dir = Environment.CurrentDirectory;

			// Find Unity assets directory
			while (!Directory.Exists(Path.Combine(dir, "Assets")) && Path.GetFullPath(dir) != Path.GetFullPath(Path.Combine(dir, "..")))
				dir = Path.Combine(dir, "..");
			m_AssetsDir = Path.Combine(dir, "Assets");
			if (!Directory.Exists(m_AssetsDir))
				throw new Exception("Could not find assets dir");
			m_ScriptsDir = Path.Combine(m_AssetsDir, "Scripts");
		}

		/// <summary>
		/// Get the list of files in a directory, or get a single file if a file is passed.
		/// </summary>
		/// <param name="path">A file or folder</param>
		/// <returns></returns>
		static IEnumerable<string> GetFileList(string path)
		{
			if (Directory.Exists(path))
				return Directory.EnumerateFiles(path, "*", new EnumerationOptions() { RecurseSubdirectories = true });
			else
				return new[] { path };
		}

		static async Task<List<KeyValuePair<string, ReferentAsset>>> FindReferencesAsync(IEnumerable<string> _searchStrings)
		{
			Init();

			// Allow directories to be passed (and explore them recursively):
			_searchStrings = _searchStrings
				.SelectMany(GetFileList)
				.Where(path => !path.EndsWith(".meta"));

			var searchFiles = m_LimitedFilesToSearch ?? Find(m_AssetsDir);

			// Find references to these assets (referent is something that is REFERRED to):
			ReferentAsset[] referents;
			try
			{
				referents = _searchStrings
				   .Select(term => new ReferentAsset(term))
				   .Distinct(new ReferentPathComparer())
				   .ToArray();
			}
			catch (NotFoundException ex)
			{
				Console.WriteLine($"Not found: {ex.searchString}");
				Environment.Exit(2);
				throw; // not actually called, but needed to keep the compiler happy
			}

			foreach (var referent in referents)
			{
				var basename = Path.GetFileName(referent.m_Path);
				if (m_AsResourcesOnly)
					Console.WriteLine($"Finding references to: {basename} (as resource)");
				else
					Console.WriteLine($"Finding references to: {basename} -- {referent.m_Guid}");
			}

			// Which path refers to which asset?
			var successfulSearches = new List<KeyValuePair<string, ReferentAsset>>();

			var _extensions =
				referents.All(s => s.m_IsScript)
				? new List<string> { ".prefab", ".unity", ".asset" }
				: new List<string> { ".asset", ".controller", ".mask", ".mat", ".overrideController", ".prefab", ".renderTexture", ".unity", ".xml" };

			if (m_SearchBinaries)
			{
				_extensions.Add(".dll");
				_extensions.Add(".bin");
				_extensions.Add(".exe"); // unity doesn't use this one
			}
			string[] extensions = _extensions.ToArray();

			if (!m_AsResourcesOnly)
			{
				Console.WriteLine();
				Console.WriteLine("Finding asset references...");

				var semaphore = new SemaphoreSlim(initialCount: m_MaxConcurrency);
				var tasks = new List<Task>();
				foreach (var filePath in searchFiles)
				{
					var extension = Path.GetExtension(filePath);
					if (extensions.Contains(extension))
						tasks.Add(FindGuidsInFileAsync(filePath, referents, successfulSearches, semaphore));
				}
				await Task.WhenAll(tasks).ConfigureAwait(false);
			}

			var resources = referents.Where(IsAssetBundleOrResource).ToList();

			if (resources.Any())
				await FindAsResources(resources, searchFiles, extensions, successfulSearches).ConfigureAwait(false);

			if (m_PrintUnreferenced)
			{
				Console.WriteLine();
				Array.Sort(referents, (ReferentAsset a, ReferentAsset b) =>
					string.Compare(a?.m_Path, b?.m_Path));
				foreach (var referent in referents)
				{
					if (referent != null && !successfulSearches.Any(kvp => kvp.Value.Matches(referent)))
					{
						if (m_AsResourcesOnly)
							PrintPath(referent.m_Path, "UNREFERENCED as resource: ");
						else
							PrintPath(referent.m_Path, "UNREFERENCED: ");
					}
				}
			}

			Console.WriteLine('\x007'); // bell

			return successfulSearches;
		}

		private static bool IsAssetBundleOrResource(ReferentAsset referent)
		{
			if (referent != null)
			{
				if (referent.m_IsAssetBundle)
					return true;
				string[] pathParts =
					Path.GetFullPath(referent.m_Path).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
				if (pathParts.Contains("Resources") || pathParts.Contains("Editor Default Resources"))
					return true;
			}

			return false;
		}

		static public async Task FindGuidsInFileAsync(
			string filePath, ReferentAsset[] referents,
			List<KeyValuePair<string, ReferentAsset>> successfulSearches,
			SemaphoreSlim semaphore)
		{
			await semaphore.WaitAsync().ConfigureAwait(false);

			try
			{
				if (m_FirstReferenceOnly)
				{
					// Delay before reading the file, and stop early if there is nothing to search for.
					// This happens when we are looking to find only the first referrer to each referent.
					if (referents.All(referent => referent == null))
						return;
				}

				using (var fstream = File.OpenRead(filePath))
				using (var reader = new StreamReader(fstream))
				{
					var data = await reader.ReadToEndAsync().ConfigureAwait(false);
					for (int i = 0; i < referents.Length; ++i)
					{
						var referent = referents[i];
						if (referent == null)
							continue;

						string guid = referent.m_Guid;
						//Console.WriteLine($"Checking if {filePath} contains {guid}");
						if (data.Contains(guid) && filePath != referent.m_Path)
						{
							lock (successfulSearches)
								successfulSearches.Add(new KeyValuePair<string, ReferentAsset>(filePath, referent));
							if (m_PrintReferrers)
								PrintPath(filePath, "", referent.m_Path);
							else
								PrintPath(filePath, "");

							if (m_FirstReferenceOnly)
							{
								lock (referents)
								{
									referents[i] = null;
								}
							}
						}
					}
				}
			}
			finally
			{
				semaphore.Release();
			}
		}

		static public IEnumerable<string> Find(string path)
		{
			return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
		}

		static public void PrintPath(string path, string prefix = "", string referent = null)
		{
			if (m_RelativeOutput)
			{
#if !UNITY // unity doesn't have Path.GetRelativePath()
				if (Path.IsPathRooted(path))
					path = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
				if (referent != null && Path.IsPathRooted(referent))
					referent = Path.GetRelativePath(Directory.GetCurrentDirectory(), referent);
#endif
			}
			else
			{
				path = Path.GetFullPath(path);
				if (referent != null)
					referent = Path.GetFullPath(referent);
			}

			path = path.Replace('\\', '/');
			if (referent != null)
				referent = referent.Replace('\\', '/');

			lock (m_WriteLock)
			{
				if (referent == null)
					Console.WriteLine(prefix + path);
				else
					Console.WriteLine($"{prefix}{path}\n\tRefers to: {referent}");
			}
		}

		// FIXME: This will need changes to find by addressable assets.
		//        The address will need to be part of the referent data.
		public static async Task FindAsResources(List<ReferentAsset> _resources, IEnumerable<string> filesToSearch, IList<string> extensions, List<KeyValuePair<string, ReferentAsset>> successfulSearches)
		{
			Console.WriteLine("\n");

			ReferentAssetWithBasename[] resources = new ReferentAssetWithBasename[_resources.Count];
			for (int i = 0; i < _resources.Count; ++i)
			{
				var referent = _resources[i];
				Console.WriteLine($"{Path.GetFileName(referent.m_Path)} is in Resources/ or an asset bundle, so also searching for references by name.");
				string basename = Path.GetFileNameWithoutExtension(referent.m_Path);
				resources[i] = new ReferentAssetWithBasename(referent, basename);
			}

			Console.WriteLine();

			var semaphore = new SemaphoreSlim(initialCount: m_MaxConcurrency);
			var tasks = new List<Task>();
			foreach (var fileToSearch in filesToSearch)
			{
				var extension = Path.GetExtension(fileToSearch);
				if (extensions.Contains(extension))
					tasks.Add(FindResourcesInFileAsync(fileToSearch, resources, successfulSearches, semaphore));
			}
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}

		private static async Task FindResourcesInFileAsync(
			string fileToSearch,
			ReferentAssetWithBasename[] referents,
			List<KeyValuePair<string, ReferentAsset>> successfulSearches,
			SemaphoreSlim semaphore)
		{
			await semaphore.WaitAsync().ConfigureAwait(false);
			try
			{
				if (m_FirstReferenceOnly)
				{
					// Stop early if possible. See comment in FindGuidsInFileAsync().
					if (referents.All(referent => referent == null))
						return;
				}

				using (var fstream = File.OpenRead(fileToSearch))
				using (var reader = new StreamReader(fstream))
				{
					string data = await reader.ReadToEndAsync().ConfigureAwait(false);
					for (int i = 0; i < referents.Length; ++i)
					{
						var referent = referents[i];
						if (referent == null)
							continue;

						//Console.WriteLine($"Checking if {filePath} contains {referent.m_Basename}");
						if (data.Contains(referent.m_Basename)
							&& Path.GetFullPath(fileToSearch) != Path.GetFullPath(referent.m_Path))
						{
							// It matches, but let's make sure it matches as a distinct word.
							// (Don't match "foobar" for resource "bar.prefab".)
							string wholeWordMatch = $@"\b{Regex.Escape(referent.m_Basename)}\b";
							if (Regex.IsMatch(data, wholeWordMatch))
							{
								if (m_PrintUnreferenced)
									lock (successfulSearches)
										successfulSearches.Add(new KeyValuePair<string, ReferentAsset>(fileToSearch, referent));
								string filename = Path.GetFileName(referent.m_Path);
								PrintPath(fileToSearch, $"Possible match for {filename}: ");
								if (m_FirstReferenceOnly)
								{
									lock (referents)
									{
										referents[i] = null;
									}
								}
							}
						}
					}
				}
			}
			finally
			{
				semaphore.Release();
			}
		}
	}

	internal class ReferentPathComparer : IEqualityComparer<ReferentAsset>
	{
		public bool Equals(ReferentAsset x, ReferentAsset y)
		{
			return Path.GetFullPath(x.m_Path) == Path.GetFullPath(y.m_Path);
		}

		public int GetHashCode(ReferentAsset obj)
		{
			return Path.GetFullPath(obj.m_Path).GetHashCode();
		}
	}

	[Serializable]
	internal class NotFoundException : Exception
	{
		public readonly string searchString;
		public NotFoundException(string searchString)
		{
			this.searchString = searchString;
		}
	}
}

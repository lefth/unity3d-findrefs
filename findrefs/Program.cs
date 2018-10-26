using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
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

			m_Guid = GetGuid(m_Path);
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
						Program.PrintPath(filePath, "\tMatches:  " + Path.GetFileName(filePath) + "  --  ");
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

		private static string GetGuid(string assetPath)
		{
			using (var fstream = File.OpenRead(assetPath + ".meta"))
			using (var reader = new StreamReader(fstream))
			{
				while (!reader.EndOfStream)
				{
					var line = reader.ReadLine();
					if (line.StartsWith("guid: "))
						return line.Substring("guid: ".Length);
				}
			}

			throw new Exception("GUID not found for .meta for asset: " + assetPath);
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
		private static bool m_AsResourcesOnly;
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

		static async Task<List<KeyValuePair<string, ReferentAsset>>> FindReferencesAsync(IEnumerable<string> _searchStrings)
		{
			Init();

            _searchStrings = _searchStrings.Where(path => !path.EndsWith(".meta"));

			var searchFiles = m_LimitedFilesToSearch ?? Find(m_AssetsDir);

			// Find references to these assets (referent is something that is REFERRED to):
			List<ReferentAsset> referents = null;
			try
			{
				referents = _searchStrings
				   .Select(term => new ReferentAsset(term))
				   .ToList();
			}
			catch (NotFoundException ex)
			{
				//throw new Exception("Not found: " + searchString.m_Path);
				Console.WriteLine("Not found: " + ex.searchString);
				Environment.Exit(2);
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

			var extensions =
				referents.All(s => s.m_IsScript)
				? new List<string> { ".prefab", ".unity", ".asset" }
				: new List<string> { ".asset", ".controller", ".mask", ".mat", ".overrideController", ".prefab", ".renderTexture", ".unity", ".xml" };

			if (m_SearchBinaries)
			{
				extensions.Add(".dll");
				extensions.Add(".bin");
				extensions.Add(".exe"); // unity doesn't use this one
			}

			if (!m_AsResourcesOnly)
			{
				Console.WriteLine();

				var tasks = new List<Task>();
				
				foreach (var filePath in searchFiles)
				{
					var extension = Path.GetExtension(filePath);
					if (extensions.Any(ext => ext.Equals(extension)))
						tasks.Add(FindGuidsInFileAsync(filePath, referents, successfulSearches));
				}

				await Task.WhenAll(tasks);
			}

			var resources = referents
				.Where(s => Path.GetFullPath(s.m_Path).Contains("Resources"));
			
			if (resources.Any())
				await FindAsResources(resources, searchFiles, extensions, successfulSearches).ConfigureAwait(false);

			if (m_PrintUnreferenced)
			{
				Console.WriteLine();
				foreach (var referent in referents)
				{
					if (!successfulSearches.Any(kvp => kvp.Value == referent))
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

		static public async Task FindGuidsInFileAsync(string filePath, List<ReferentAsset> referents, List<KeyValuePair<string, ReferentAsset>> successfulSearches)
		{
			using (var fstream = File.OpenRead(filePath))
			using (var reader = new StreamReader(fstream))
			{
				var data = await reader.ReadToEndAsync().ConfigureAwait(false);
				for (int i = 0; i < referents.Count; ++i)
				{
					var guid = referents[i].m_Guid;
					//Console.WriteLine("checking if "+filePath+" contains "+guid);
					if (data.Contains(guid) && filePath != referents[i].m_Path)
					{
						lock (successfulSearches)
							successfulSearches.Add(new KeyValuePair<string, ReferentAsset>(filePath, referents[i]));
						PrintPath(filePath);
						//return; // NOTE: don't break, because a file can match more than one resource
					}
				}
			}
		}

		static public IEnumerable<string> Find(string path)
		{
			return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
		}

		static public void PrintPath(string path, string prefix = "")
		{
			if (m_RelativeOutput && Path.IsPathRooted(path))
				path = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
			else
				path = Path.GetFullPath(path);

			lock (m_WriteLock)
			{
				Console.WriteLine(prefix + path.Replace('\\', '/'));
			}
		}

		public static async Task FindAsResources(IEnumerable<ReferentAsset> resources, IEnumerable<string> filesToSearch, IList<string> extensions, List<KeyValuePair<string, ReferentAsset>> successfulSearches)
		{
			Console.WriteLine("\n");
			foreach (var resource in resources)
				Console.WriteLine(Path.GetFileName(resource.m_Path) + " is in Resources/, so also searching for references by name.");

			List<string> nameWithoutExt = resources
				.Select(r => Path.GetFileNameWithoutExtension(r.m_Path))
				.ToList();

			Console.WriteLine();

			var tasks = new List<Task>();
			foreach (var fileToSearch in filesToSearch)
			{
				var extension = Path.GetExtension(fileToSearch);
				if (extensions.Any(ext => ext.Equals(extension)))
					tasks.Add(FindAsResourcesAsync(fileToSearch, resources, nameWithoutExt, successfulSearches));
			}
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}

		private static async Task FindAsResourcesAsync(string fileToSearch, IEnumerable<ReferentAsset> referents, List<string> nameWithoutExt, List<KeyValuePair<string, ReferentAsset>> successfulSearches)
		{
			using (var fstream = File.OpenRead(fileToSearch))
			using (var reader = new StreamReader(fstream))
			{
				string data = await reader.ReadToEndAsync().ConfigureAwait(false);
				int i = -1;
				foreach (var referent in referents)
				{
					++i;
					string _nameWithoutExt = nameWithoutExt[i];
					//Console.WriteLine("checking if " + filePath + " contains " + _nameWithoutExt);
					if (data.Contains(_nameWithoutExt)
						//&& Regex.IsMatch(data, $@"\b{_nameWithoutExt}\b")
						&& Path.GetFullPath(fileToSearch) != Path.GetFullPath(referent.m_Path))
					{
						if (m_PrintUnreferenced)
							successfulSearches.Add(new KeyValuePair<string, ReferentAsset>(fileToSearch, referent));
						string basename = Path.GetFileName(referent.m_Path);
						PrintPath(fileToSearch, "possible match for " + basename + ": ");

						//break; // NOTE: don't break, because a file can match more than one resource
					}
				}
			}
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

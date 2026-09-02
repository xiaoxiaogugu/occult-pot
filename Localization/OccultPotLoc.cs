using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace OccultPot.Localization;

internal static class OccultPotLoc
{
	private static readonly Dictionary<string, string> Strings = Load();

	internal static string Get(string key)
	{
		if (!Strings.TryGetValue(key, out string value))
		{
			return key;
		}
		return value;
	}

	internal static string Format(string key, params object[] args)
	{
		string text = Get(key);
		try
		{
			return string.Format(text, args);
		}
		catch
		{
			return text;
		}
	}

	private static Dictionary<string, string> Load()
	{
		Assembly executingAssembly = Assembly.GetExecutingAssembly();
		string text = executingAssembly.GetManifestResourceNames().FirstOrDefault((string n) => n.EndsWith("zh-CN.json", StringComparison.Ordinal));
		if (text == null)
		{
			return new Dictionary<string, string>();
		}
		using Stream stream = executingAssembly.GetManifestResourceStream(text);
		if (stream == null)
		{
			return new Dictionary<string, string>();
		}
		return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
	}
}

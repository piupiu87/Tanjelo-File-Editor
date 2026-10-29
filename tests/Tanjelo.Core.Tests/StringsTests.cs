using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Tanjelo.Core.Tests;

/// <summary>Each Strings.resx and its Strings.cs list the same keys, so no word is missing or left over.</summary>
public class StringsTests
{
    [Theory]
    [InlineData(typeof(Strings))]
    [InlineData(typeof(Tanjelo.Strings))]
    public void Every_string_in_the_resource_file_has_a_property_and_every_property_a_string(Type strings)
    {
        var resources = new ResourceManager(strings.FullName!, strings.Assembly);
        var inFile = resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)!
            .Cast<DictionaryEntry>().Select(e => (string)e.Key).Order().ToList();
        var properties = strings.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order().ToList();
        Assert.Empty(inFile.Except(properties));
        Assert.Empty(properties.Except(inFile));
        Assert.All(properties, name => Assert.False(string.IsNullOrEmpty((string)strings.GetProperty(name)!.GetValue(null)!), name));
    }

    [Fact]
    public void Placeholders_in_the_strings_are_numbered_from_zero_without_gaps()
    {
        foreach (var strings in new[] { typeof(Strings), typeof(Tanjelo.Strings) })
            foreach (var property in strings.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)))
            {
                var text = (string)property.GetValue(null)!;
                // JSON in a hint uses braces of its own, never with a number straight after.
                var numbers = System.Text.RegularExpressions.Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})")
                    .Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();
                Assert.True(numbers.SequenceEqual(Enumerable.Range(0, numbers.Count)), $"{strings.Name}.{property.Name}: \"{text}\"");
            }
    }
}

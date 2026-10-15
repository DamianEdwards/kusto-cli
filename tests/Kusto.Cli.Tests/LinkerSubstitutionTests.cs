using System.Reflection;
using System.Xml.Linq;
using Hex1b;

namespace Kusto.Cli.Tests;

public sealed class LinkerSubstitutionTests
{
    [Fact]
    public void Substitutions_MatchDependencyMembers()
    {
        var directory = AppContext.BaseDirectory;
        string? path = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "src", "Kusto.Cli", "ILLink.Substitutions.xml");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(path);
        var document = XDocument.Load(path);
        var assemblies = document.Root!.Elements("assembly").ToArray();
        Assert.NotEmpty(assemblies);
        foreach (var element in assemblies)
        {
            var assembly = Assembly.Load((string)element.Attribute("fullname")!);
            foreach (var typeElement in element.Elements("type"))
            {
                var type = assembly.GetType((string)typeElement.Attribute("fullname")!, throwOnError: true)!;
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var members = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)).ToArray();
                foreach (var methodElement in typeElement.Elements("method"))
                {
                    var signature = (string)methodElement.Attribute("signature")!;
                    var member = Assert.Single(members, member => GetSignature(member) == signature);
                    if ((string?)methodElement.Attribute("body") == "stub")
                    {
                        var getter = Assert.IsAssignableFrom<MethodInfo>(member);
                        Assert.Equal(typeof(bool), getter.ReturnType);
                        Assert.Equal("false", (string?)methodElement.Attribute("value"));
                        Assert.Empty(getter.GetParameters());
                        Assert.False(Assert.IsType<bool>(getter.Invoke(Activator.CreateInstance(type), null)));
                    }
                    else
                    {
                        Assert.Equal("remove", (string?)methodElement.Attribute("body"));
                    }
                }
            }
        }
    }

    [Fact]
    public void HeadlessRendererDefaults_DoNotEnableSurfacePooling()
    {
        Assert.False(new Hex1bAppOptions().EnableSurfacePooling);
    }

    [Fact]
    public async Task SelfTest_ExercisesSyntaxAndHeadlessRendering()
    {
        await LinkerSubstitutionSelfTest.RunAsync(CancellationToken.None);
    }

    private static string GetSignature(MethodBase method)
    {
        var returnType = method is MethodInfo info ? FormatType(info.ReturnType) : "System.Void";
        return $"{returnType} {method.Name}({string.Join(",", method.GetParameters().Select(parameter => FormatType(parameter.ParameterType)))})";
    }

    private static string FormatType(Type type)
    {
        return type.IsGenericType
            ? $"{type.GetGenericTypeDefinition().FullName}<{string.Join(",", type.GetGenericArguments().Select(FormatType))}>"
            : type.FullName!;
    }
}

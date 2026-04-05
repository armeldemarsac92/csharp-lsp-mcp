namespace CSharpLspMcp.Tests;

internal static class TestWorkspaceFactory
{
    public static string CreateChangeImpactWorkspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), $"change-impact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);

        var coreDirectory = Path.Combine(workspacePath, "src", "Sample.Core");
        var appDirectory = Path.Combine(workspacePath, "src", "Sample.App");
        var testsDirectory = Path.Combine(workspacePath, "tests", "Sample.App.Tests");
        Directory.CreateDirectory(coreDirectory);
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(testsDirectory);

        File.WriteAllText(
            Path.Combine(coreDirectory, "Sample.Core.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(coreDirectory, "IWeatherService.cs"),
            """
            namespace Sample.Core;

            public interface IWeatherService
            {
                string GetForecast();
            }
            """);

        File.WriteAllText(
            Path.Combine(appDirectory, "Sample.App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Sample.Core\Sample.Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(appDirectory, "Program.cs"),
            """
            using Sample.App;

            Console.WriteLine(new WeatherReporter().Report());
            """);
        File.WriteAllText(
            Path.Combine(appDirectory, "DependencyInjection.cs"),
            """
            using Microsoft.Extensions.DependencyInjection;
            using Sample.Core;

            namespace Sample.App;

            public static class DependencyInjection
            {
                public static void AddApp(IServiceCollection services)
                {
                    services.AddSingleton<IWeatherService, WeatherService>();
                }
            }
            """);
        File.WriteAllText(
            Path.Combine(appDirectory, "DummyDependencyInjection.cs"),
            """
            namespace Microsoft.Extensions.DependencyInjection;

            public interface IServiceCollection
            {
            }

            public sealed class ServiceCollection : IServiceCollection
            {
            }

            public static class ServiceCollectionServiceExtensions
            {
                public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services)
                    where TImplementation : TService
                    => services;
            }
            """);
        File.WriteAllText(
            Path.Combine(appDirectory, "WeatherService.cs"),
            """
            using Sample.Core;

            namespace Sample.App;

            public sealed class WeatherService : IWeatherService
            {
                public string GetForecast() => "sunny";
            }

            public sealed class WeatherReporter
            {
                public string Report()
                {
                    IWeatherService service = new WeatherService();
                    return service.GetForecast();
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(testsDirectory, "Sample.App.Tests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\..\src\Sample.App\Sample.App.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(testsDirectory, "WeatherServiceTests.cs"),
            """
            using Sample.App;
            using Sample.Core;

            namespace Sample.App.Tests;

            public sealed class WeatherServiceTests
            {
                public string ShouldCallTheService()
                {
                    IWeatherService service = new WeatherService();
                    return service.GetForecast();
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(workspacePath, "Sample.sln"),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.Core", "src\Sample.Core\Sample.Core.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.App", "src\Sample.App\Sample.App.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.App.Tests", "tests\Sample.App.Tests\Sample.App.Tests.csproj", "{33333333-3333-3333-3333-333333333333}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Release|Any CPU = Release|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.Build.0 = Release|Any CPU
            	EndGlobalSection
            	GlobalSection(SolutionProperties) = preSolution
            		HideSolutionNode = FALSE
            	EndGlobalSection
            EndGlobal
            """);

        return workspacePath;
    }
}

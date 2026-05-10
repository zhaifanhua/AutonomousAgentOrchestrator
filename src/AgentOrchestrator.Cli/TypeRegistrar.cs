using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace AgentOrchestrator.Cli;

/// <summary>
/// Spectre.Console DI 集成：将 IServiceProvider 桥接到 TypeRegistrar
/// </summary>
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    private IServiceProvider? _provider;

    public ITypeResolver Build()
    {
        _provider = services.BuildServiceProvider();
        return new TypeResolver(_provider);
    }

    public void Register(Type service, Type implementation) =>
        services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) =>
        services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) =>
        services.AddSingleton(service, _ => factory());
}

public sealed class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type) =>
        type == null ? null : provider.GetService(type);

    public void Dispose()
    {
        if (provider is IDisposable d)
        {
            d.Dispose();
        }
    }
}
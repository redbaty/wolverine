using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.CodeGen;

public class FromFileStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.ParameterType == typeof(IFormFile))
        {
            var frame = new FromFileValue(parameter);
                chain.Middleware.Add(frame);
                variable = frame.Variable;
            return true;
        } else if (parameter.ParameterType == typeof(IFormFileCollection))
        {
            var frame = new FromFileValues(parameter);
                chain.Middleware.Add(frame);
                variable = frame.Variable;
            return true;
        }
        variable = null;
        return false;
    }
}

internal class FromFileValue : SyncFrame
{
    private Variable? _httpContext;
    public FromFileValue(ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Retrieve header value from the request");
        writer.Write(
            $"var {Variable.Usage} = {nameof(HttpHandler.ReadSingleFormFileValue)}({_httpContext!.Usage});");
        Next?.GenerateCode(method, writer);
    }
}

internal class FromFileValues : SyncFrame
{
    private Variable? _httpContext;
    public FromFileValues(ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Retrieve header value from the request");
        writer.Write(
            $"var {Variable.Usage} = {nameof(HttpHandler.ReadManyFormFileValues)}({_httpContext!.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
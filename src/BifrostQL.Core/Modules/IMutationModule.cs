using BifrostQL.Model;
using GraphQL;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules
{
    public interface IMutationModule
    {
        public void OnSave(IResolveFieldContext context);
        public string[] Insert(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model);
        public string[] Update(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model);
        public string[] Delete(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model);
    }


    public interface IMutationModules : IReadOnlyCollection<IMutationModule>, IMutationModule
    {
    };

    public class ModulesWrap : IMutationModules
    {
        public IReadOnlyCollection<IMutationModule> Modules { get; init; } = null!;
        public int Count => Modules.Count;

        public string[] Delete(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            return Modules.SelectMany(module => module.Delete(data, table, userContext, model)).ToArray();
        }

        public IEnumerator<IMutationModule> GetEnumerator()
        {
            return Modules.GetEnumerator();
        }

        public string[] Insert(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            return Modules.SelectMany(module => module.Insert(data, table, userContext, model)).ToArray();
        }

        public void OnSave(IResolveFieldContext context)
        {
            foreach (var module in Modules)
            {
                module.OnSave(context);
            }
        }

        public string[] Update(Dictionary<string, object?> data, IDbTable table, IDictionary<string, object?> userContext, IDbModel model)
        {
            return Modules.SelectMany(module => module.Update(data, table, userContext, model)).ToArray();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Modules.GetEnumerator();
        }

    }
}

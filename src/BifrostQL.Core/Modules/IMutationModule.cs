using BifrostQL.Model;
using GraphQL;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Modules
{
    public interface IMutationModule
    {
        public void OnSave(IResolveFieldContext context);
        public string[] Insert(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext);
        public string[] Update(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext);
        public string[] Delete(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext);
    }


    public interface IMutationModules : IReadOnlyCollection<IMutationModule>, IMutationModule
    {
    };

    public class ModulesWrap : IMutationModules
    {
        public IReadOnlyCollection<IMutationModule> Modules { get; init; } = null!;
        public int Count => Modules.Count;

        public string[] Delete(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext)
        {
            return Modules.SelectMany(module => module.Delete(data, table, userContext)).ToArray();
        }

        public IEnumerator<IMutationModule> GetEnumerator()
        {
            return Modules.GetEnumerator();
        }

        public string[] Insert(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext)
        {
            return Modules.SelectMany(module => module.Insert(data, table, userContext)).ToArray();
        }

        public void OnSave(IResolveFieldContext context)
        {
            foreach(var module in Modules)
            {
                module.OnSave(context);
            }
        }

        public string[] Update(Dictionary<string, object?> data, TableDto table, IDictionary<string, object?> userContext)
        {
            return Modules.SelectMany(module => module.Update(data, table, userContext)).ToArray();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Modules.GetEnumerator();
        }

    }
}

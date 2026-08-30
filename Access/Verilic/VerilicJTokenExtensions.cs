using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace S1Jarvis.Access.Verilic
{
    internal static class VerilicJTokenExtensions
    {
        internal static IEnumerable<JToken> DescendantsAndSelf(this JToken token)
        {
            if (token == null)
                yield break;

            yield return token;

            var container = token as JContainer;
            if (container == null)
                yield break;

            foreach (JToken child in container.Children())
            {
                foreach (JToken descendant in child.DescendantsAndSelf())
                    yield return descendant;
            }
        }
    }
}

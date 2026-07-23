#if NEWTONSOFT_EXISTS
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DingoGameObjectsCMS.Serialization
{
    internal sealed class RequiredCamelCaseContractResolver : DefaultContractResolver
    {
        public RequiredCamelCaseContractResolver()
        {
            NamingStrategy = new CamelCaseNamingStrategy();
        }

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);
            property.Required = Required.Always;
            return property;
        }
    }
}
#endif

//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------
namespace Orleans.Hosting
{
    public static partial class SiloPersistentStreamConfiguratorExtension
    {
        public static void UseDynamicAzureDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator, System.TimeSpan? siloMaturityPeriod = null) { }

        public static void UseStaticAzureDeploymentBalancer(this ISiloPersistentStreamConfigurator configurator, System.TimeSpan? siloMaturityPeriod = null) { }
    }
}

namespace Orleans.Runtime.Host
{
    public partial interface IServiceRuntimeWrapper
    {
        string DeploymentId { get; }

        int FaultDomain { get; }

        string InstanceName { get; }

        int RoleInstanceCount { get; }

        string RoleName { get; }

        int UpdateDomain { get; }

        string GetConfigurationSettingValue(string configurationSettingName);
        System.Net.IPEndPoint GetIPEndpoint(string endpointName);
        void SubscribeForStoppingNotification(object handlerObject, System.EventHandler<object> handler);
        void UnsubscribeFromStoppingNotification(object handlerObject, System.EventHandler<object> handler);
    }
}
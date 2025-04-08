//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------
namespace Orleans.Clustering.AzureStorage
{
    public partial class AzureStorageClusteringOptions : AzureStorageOperationOptions
    {
        public const string DEFAULT_TABLE_NAME = "OrleansSiloInstances";
        public override string TableName { get { throw null; } set { } }
    }

    public partial class AzureStorageClusteringOptionsValidator : AzureStorageOperationOptionsValidator<AzureStorageClusteringOptions>
    {
        public AzureStorageClusteringOptionsValidator(AzureStorageClusteringOptions options, string name) : base(default!, default!) { }
    }

    public partial class AzureStorageGatewayOptions : AzureStorageOperationOptions
    {
        public override string TableName { get { throw null; } set { } }
    }

    public partial class AzureStorageGatewayOptionsValidator : AzureStorageOperationOptionsValidator<AzureStorageGatewayOptions>
    {
        public AzureStorageGatewayOptionsValidator(AzureStorageGatewayOptions options, string name) : base(default!, default!) { }
    }

    public partial class AzureStorageOperationOptions
    {
        public Azure.Data.Tables.TableClientOptions ClientOptions { get { throw null; } set { } }

        public AzureStoragePolicyOptions StoragePolicyOptions { get { throw null; } }

        public virtual string TableName { get { throw null; } set { } }

        public Azure.Data.Tables.TableServiceClient TableServiceClient { get { throw null; } set { } }

        [System.Obsolete("Set the TableServiceClient property directly.")]
        public void ConfigureTableServiceClient(System.Func<System.Threading.Tasks.Task<Azure.Data.Tables.TableServiceClient>> createClientCallback) { }

        [System.Obsolete("Set the TableServiceClient property directly.")]
        public void ConfigureTableServiceClient(string connectionString) { }

        [System.Obsolete("Set the TableServiceClient property directly.")]
        public void ConfigureTableServiceClient(System.Uri serviceUri, Azure.AzureSasCredential azureSasCredential) { }

        [System.Obsolete("Set the TableServiceClient property directly.")]
        public void ConfigureTableServiceClient(System.Uri serviceUri, Azure.Core.TokenCredential tokenCredential) { }

        [System.Obsolete("Set the TableServiceClient property directly.")]
        public void ConfigureTableServiceClient(System.Uri serviceUri, Azure.Data.Tables.TableSharedKeyCredential sharedKeyCredential) { }

        [System.Obsolete("Set the TableServiceClient property directly.")]
        public void ConfigureTableServiceClient(System.Uri serviceUri) { }
    }

    public partial class AzureStorageOperationOptionsValidator<TOptions> : IConfigurationValidator where TOptions : AzureStorageOperationOptions
    {
        public AzureStorageOperationOptionsValidator(TOptions options, string name = null) { }

        public string Name { get { throw null; } }

        public TOptions Options { get { throw null; } }

        public virtual void ValidateConfiguration() { }
    }

    public partial class AzureStoragePolicyOptions
    {
        public System.TimeSpan CreationTimeout { get { throw null; } set { } }

        public int MaxBulkUpdateRows { get { throw null; } set { } }

        public int MaxCreationRetries { get { throw null; } set { } }

        public int MaxOperationRetries { get { throw null; } set { } }

        public System.TimeSpan OperationTimeout { get { throw null; } set { } }

        public System.TimeSpan PauseBetweenCreationRetries { get { throw null; } set { } }

        public System.TimeSpan PauseBetweenOperationRetries { get { throw null; } set { } }
    }
}

namespace Orleans.Hosting
{
    public static partial class AzureTableClusteringExtensions
    {
        public static IClientBuilder UseAzureStorageClustering(this IClientBuilder builder, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Clustering.AzureStorage.AzureStorageGatewayOptions>> configureOptions) { throw null; }

        public static IClientBuilder UseAzureStorageClustering(this IClientBuilder builder, System.Action<Clustering.AzureStorage.AzureStorageGatewayOptions> configureOptions) { throw null; }

        public static ISiloBuilder UseAzureStorageClustering(this ISiloBuilder builder, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Clustering.AzureStorage.AzureStorageClusteringOptions>> configureOptions) { throw null; }

        public static ISiloBuilder UseAzureStorageClustering(this ISiloBuilder builder, System.Action<Clustering.AzureStorage.AzureStorageClusteringOptions> configureOptions) { throw null; }
    }
}
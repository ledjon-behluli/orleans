//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------
namespace Orleans.Configuration
{
    public partial class AzureBlobStorageOptions : Storage.IStorageProviderSerializerOptions
    {
        public const string DEFAULT_CONTAINER_NAME = "grainstate";
        public const int DEFAULT_INIT_STAGE = 10000;
        public Azure.Storage.Blobs.BlobServiceClient BlobServiceClient { get { throw null; } set { } }

        public System.Func<System.IServiceProvider, AzureBlobStorageOptions, Storage.IBlobContainerFactory> BuildContainerFactory { get { throw null; } set { } }

        public Azure.Storage.Blobs.BlobClientOptions ClientOptions { get { throw null; } set { } }

        public string ContainerName { get { throw null; } set { } }

        public bool DeleteStateOnClear { get { throw null; } set { } }

        public Storage.IGrainStorageSerializer GrainStorageSerializer { get { throw null; } set { } }

        public int InitStage { get { throw null; } set { } }

        [System.Obsolete("Set the BlobServiceClient property directly.")]
        public void ConfigureBlobServiceClient(System.Func<System.Threading.Tasks.Task<Azure.Storage.Blobs.BlobServiceClient>> createClientCallback) { }

        [System.Obsolete("Set the BlobServiceClient property directly.")]
        public void ConfigureBlobServiceClient(string connectionString) { }

        [System.Obsolete("Set the BlobServiceClient property directly.")]
        public void ConfigureBlobServiceClient(System.Uri serviceUri, Azure.AzureSasCredential azureSasCredential) { }

        [System.Obsolete("Set the BlobServiceClient property directly.")]
        public void ConfigureBlobServiceClient(System.Uri serviceUri, Azure.Core.TokenCredential tokenCredential) { }

        [System.Obsolete("Set the BlobServiceClient property directly.")]
        public void ConfigureBlobServiceClient(System.Uri serviceUri, Azure.Storage.StorageSharedKeyCredential sharedKeyCredential) { }

        [System.Obsolete("Set the BlobServiceClient property directly.")]
        public void ConfigureBlobServiceClient(System.Uri serviceUri) { }
    }

    public partial class AzureBlobStorageOptionsValidator : IConfigurationValidator
    {
        public AzureBlobStorageOptionsValidator(AzureBlobStorageOptions options, string name) { }

        public void ValidateConfiguration() { }
    }

    public partial class AzureTableGrainStorageOptionsValidator : Persistence.AzureStorage.AzureStorageOperationOptionsValidator<AzureTableStorageOptions>
    {
        public AzureTableGrainStorageOptionsValidator(AzureTableStorageOptions options, string name) : base(default!, default!) { }
    }

    public partial class AzureTableStorageOptions : Persistence.AzureStorage.AzureStorageOperationOptions, Storage.IStorageProviderSerializerOptions
    {
        public const int DEFAULT_INIT_STAGE = 10000;
        public const string DEFAULT_TABLE_NAME = "OrleansGrainState";
        public bool DeleteStateOnClear { get { throw null; } set { } }

        public Storage.IGrainStorageSerializer GrainStorageSerializer { get { throw null; } set { } }

        public int InitStage { get { throw null; } set { } }

        public override string TableName { get { throw null; } set { } }

        public bool UseStringFormat { get { throw null; } set { } }
    }
}

namespace Orleans.Hosting
{
    public static partial class AzureBlobGrainStorageServiceCollectionExtensions
    {
        public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAzureBlobGrainStorage(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, string name, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Configuration.AzureBlobStorageOptions>> configureOptions = null) { throw null; }

        public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAzureBlobGrainStorage(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, string name, System.Action<Configuration.AzureBlobStorageOptions> configureOptions) { throw null; }

        public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAzureBlobGrainStorageAsDefault(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Configuration.AzureBlobStorageOptions>> configureOptions = null) { throw null; }

        public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAzureBlobGrainStorageAsDefault(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, System.Action<Configuration.AzureBlobStorageOptions> configureOptions) { throw null; }
    }

    public static partial class AzureBlobSiloBuilderExtensions
    {
        public static ISiloBuilder AddAzureBlobGrainStorage(this ISiloBuilder builder, string name, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Configuration.AzureBlobStorageOptions>> configureOptions = null) { throw null; }

        public static ISiloBuilder AddAzureBlobGrainStorage(this ISiloBuilder builder, string name, System.Action<Configuration.AzureBlobStorageOptions> configureOptions) { throw null; }

        public static ISiloBuilder AddAzureBlobGrainStorageAsDefault(this ISiloBuilder builder, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Configuration.AzureBlobStorageOptions>> configureOptions = null) { throw null; }

        public static ISiloBuilder AddAzureBlobGrainStorageAsDefault(this ISiloBuilder builder, System.Action<Configuration.AzureBlobStorageOptions> configureOptions) { throw null; }
    }

    public static partial class AzureTableSiloBuilderExtensions
    {
        public static ISiloBuilder AddAzureTableGrainStorage(this ISiloBuilder builder, string name, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Configuration.AzureTableStorageOptions>> configureOptions = null) { throw null; }

        public static ISiloBuilder AddAzureTableGrainStorage(this ISiloBuilder builder, string name, System.Action<Configuration.AzureTableStorageOptions> configureOptions) { throw null; }

        public static ISiloBuilder AddAzureTableGrainStorageAsDefault(this ISiloBuilder builder, System.Action<Microsoft.Extensions.Options.OptionsBuilder<Configuration.AzureTableStorageOptions>> configureOptions = null) { throw null; }

        public static ISiloBuilder AddAzureTableGrainStorageAsDefault(this ISiloBuilder builder, System.Action<Configuration.AzureTableStorageOptions> configureOptions) { throw null; }
    }
}

namespace Orleans.Persistence.AzureStorage
{
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

namespace Orleans.Storage
{
    public partial class AzureBlobGrainStorage : IGrainStorage, ILifecycleParticipant<Runtime.ISiloLifecycle>
    {
        public AzureBlobGrainStorage(string name, Configuration.AzureBlobStorageOptions options, IBlobContainerFactory blobContainerFactory, Serialization.Serializers.IActivatorProvider activatorProvider, Microsoft.Extensions.Logging.ILogger<AzureBlobGrainStorage> logger) { }

        public System.Threading.Tasks.Task ClearStateAsync<T>(string grainType, Runtime.GrainId grainId, IGrainState<T> grainState) { throw null; }

        public void Participate(Runtime.ISiloLifecycle lifecycle) { }

        public System.Threading.Tasks.Task ReadStateAsync<T>(string grainType, Runtime.GrainId grainId, IGrainState<T> grainState) { throw null; }

        public System.Threading.Tasks.Task WriteStateAsync<T>(string grainType, Runtime.GrainId grainId, IGrainState<T> grainState) { throw null; }
    }

    public static partial class AzureBlobGrainStorageFactory
    {
        public static AzureBlobGrainStorage Create(System.IServiceProvider services, string name) { throw null; }
    }

    public partial class AzureTableGrainStorage : IGrainStorage, IRestExceptionDecoder, ILifecycleParticipant<Runtime.ISiloLifecycle>
    {
        public AzureTableGrainStorage(string name, Configuration.AzureTableStorageOptions options, Microsoft.Extensions.Options.IOptions<Configuration.ClusterOptions> clusterOptions, Microsoft.Extensions.Logging.ILogger<AzureTableGrainStorage> logger, Serialization.Serializers.IActivatorProvider activatorProvider) { }

        public System.Threading.Tasks.Task ClearStateAsync<T>(string grainType, Runtime.GrainId grainId, IGrainState<T> grainState) { throw null; }

        public bool DecodeException(System.Exception e, out System.Net.HttpStatusCode httpStatusCode, out string restStatus, bool getRESTErrors = false) { throw null; }

        public void Participate(Runtime.ISiloLifecycle lifecycle) { }

        public System.Threading.Tasks.Task ReadStateAsync<T>(string grainType, Runtime.GrainId grainId, IGrainState<T> grainState) { throw null; }

        public System.Threading.Tasks.Task WriteStateAsync<T>(string grainType, Runtime.GrainId grainId, IGrainState<T> grainState) { throw null; }
    }

    public static partial class AzureTableGrainStorageFactory
    {
        public static AzureTableGrainStorage Create(System.IServiceProvider services, string name) { throw null; }
    }

    public partial interface IBlobContainerFactory
    {
        Azure.Storage.Blobs.BlobContainerClient GetBlobContainerClient(Runtime.GrainId grainId);
        System.Threading.Tasks.Task InitializeAsync(Azure.Storage.Blobs.BlobServiceClient client);
    }

    [GenerateSerializer]
    public partial class TableStorageUpdateConditionNotSatisfiedException : InconsistentStateException
    {
        public TableStorageUpdateConditionNotSatisfiedException() { }

        [System.Obsolete]
        protected TableStorageUpdateConditionNotSatisfiedException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }

        public TableStorageUpdateConditionNotSatisfiedException(string msg, System.Exception exc) { }

        public TableStorageUpdateConditionNotSatisfiedException(string grainType, string grainId, string tableName, string storedEtag, string currentEtag, System.Exception storageException) { }

        public TableStorageUpdateConditionNotSatisfiedException(string errorMsg, string grainType, string grainId, string tableName, string storedEtag, string currentEtag, System.Exception storageException) { }

        public TableStorageUpdateConditionNotSatisfiedException(string msg) { }

        [Id(0)]
        public string GrainId { get { throw null; } }

        [Id(1)]
        public string GrainType { get { throw null; } }

        [Id(2)]
        public string TableName { get { throw null; } }

        [System.Obsolete]
        public override void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) { }
    }
}

namespace OrleansCodeGen.Orleans.Storage
{
    [System.CodeDom.Compiler.GeneratedCode("OrleansCodeGen", "9.0.0.0")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public sealed partial class Codec_TableStorageUpdateConditionNotSatisfiedException : global::Orleans.Serialization.Codecs.IFieldCodec<global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException>, global::Orleans.Serialization.Codecs.IFieldCodec, global::Orleans.Serialization.Serializers.IBaseCodec<global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException>, global::Orleans.Serialization.Serializers.IBaseCodec
    {
        public Codec_TableStorageUpdateConditionNotSatisfiedException(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider) { }

        public void Deserialize<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException instance) { }

        public global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException ReadValue<TReaderInput>(ref global::Orleans.Serialization.Buffers.Reader<TReaderInput> reader, global::Orleans.Serialization.WireProtocol.Field field) { throw null; }

        public void Serialize<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException instance)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> { }

        public void WriteField<TBufferWriter>(ref global::Orleans.Serialization.Buffers.Writer<TBufferWriter> writer, uint fieldIdDelta, System.Type expectedType, global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException value)
            where TBufferWriter : System.Buffers.IBufferWriter<byte> { }
    }

    [System.CodeDom.Compiler.GeneratedCode("OrleansCodeGen", "9.0.0.0")]
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public sealed partial class Copier_TableStorageUpdateConditionNotSatisfiedException : global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.ExceptionCopier<global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException, global::Orleans.Storage.InconsistentStateException>
    {
        public Copier_TableStorageUpdateConditionNotSatisfiedException(global::Orleans.Serialization.Serializers.ICodecProvider codecProvider) : base(default(Serialization.Serializers.ICodecProvider)!) { }

        public override void DeepCopy(global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException input, global::Orleans.Storage.TableStorageUpdateConditionNotSatisfiedException output, global::Orleans.Serialization.Cloning.CopyContext context) { }
    }
}
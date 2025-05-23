using Data;
using Data.Systems;
using Fonts;
using Fonts.Systems;
using Meshes;
using Rendering;
using Simulation.Tests;
using Textures;
using Types;
using Worlds;

namespace TextRendering.Systems.Tests
{
    public abstract class TextRenderingSystemsTests : SimulationTests
    {
        static TextRenderingSystemsTests()
        {
            MetadataRegistry.Load<TextRenderingMetadataBank>();
            MetadataRegistry.Load<RenderingMetadataBank>();
            MetadataRegistry.Load<DataMetadataBank>();
            MetadataRegistry.Load<FontsMetadataBank>();
            MetadataRegistry.Load<TexturesMetadataBank>();
            MetadataRegistry.Load<MeshesMetadataBank>();
        }

        protected override Schema CreateSchema()
        {
            Schema schema = base.CreateSchema();
            schema.Load<TextRenderingSchemaBank>();
            schema.Load<RenderingSchemaBank>();
            schema.Load<DataSchemaBank>();
            schema.Load<FontsSchemaBank>();
            schema.Load<TexturesSchemaBank>();
            schema.Load<MeshesSchemaBank>();
            return schema;
        }

        protected override void SetUp()
        {
            base.SetUp();
            simulator.Add(new DataImportSystem());
            simulator.Add(new FontImportSystem());
            simulator.Add(new TextMeshGenerationSystem());
        }

        protected override void TearDown()
        {
            simulator.Remove<TextMeshGenerationSystem>();
            simulator.Remove<FontImportSystem>();
            simulator.Remove<DataImportSystem>();
            base.TearDown();
        }
    }
}

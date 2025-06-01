using Data;
using Data.Messages;
using Data.Systems;
using Fonts;
using Fonts.Systems;
using Meshes;
using Rendering;
using Simulation.Tests;
using Textures;
using Types;
using Worlds;
using Worlds.Messages;

namespace TextRendering.Systems.Tests
{
    public abstract class TextRenderingSystemsTests : SimulationTests
    {
        public World world;

        static TextRenderingSystemsTests()
        {
            MetadataRegistry.Load<TextRenderingMetadataBank>();
            MetadataRegistry.Load<RenderingMetadataBank>();
            MetadataRegistry.Load<DataMetadataBank>();
            MetadataRegistry.Load<FontsMetadataBank>();
            MetadataRegistry.Load<TexturesMetadataBank>();
            MetadataRegistry.Load<MeshesMetadataBank>();
        }

        protected override void SetUp()
        {
            base.SetUp();
            Schema schema = new();
            schema.Load<TextRenderingSchemaBank>();
            schema.Load<RenderingSchemaBank>();
            schema.Load<DataSchemaBank>();
            schema.Load<FontsSchemaBank>();
            schema.Load<TexturesSchemaBank>();
            schema.Load<MeshesSchemaBank>();
            world = new(schema);
            Simulator.Add(new DataImportSystem(Simulator, world));
            Simulator.Add(new FontImportSystem(Simulator, world));
            Simulator.Add(new TextMeshGenerationSystem(Simulator, world));
        }

        protected override void TearDown()
        {
            Simulator.Remove<TextMeshGenerationSystem>();
            Simulator.Remove<FontImportSystem>();
            Simulator.Remove<DataImportSystem>();
            world.Dispose();
            base.TearDown();
        }

        override protected void Update(double deltaTime)
        {
            Simulator.Broadcast(new DataUpdate(deltaTime));
            Simulator.Broadcast(new Update(deltaTime));
        }
    }
}
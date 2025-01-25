using Data.Systems;
using Fonts.Systems;
using Simulation.Tests;
using Types;
using Worlds;

namespace TextRendering.Systems.Tests
{
    public abstract class TextRenderingSystemsTests : SimulationTests
    {
        static TextRenderingSystemsTests()
        {
            TypeRegistry.Load<TextRendering.TypeBank>();
            TypeRegistry.Load<Data.Core.TypeBank>();
            TypeRegistry.Load<Fonts.TypeBank>();
            TypeRegistry.Load<Textures.TypeBank>();
            TypeRegistry.Load<Meshes.TypeBank>();
        }

        protected override Schema CreateSchema()
        {
            Schema schema = base.CreateSchema();
            schema.Load<TextRendering.SchemaBank>();
            schema.Load<Data.Core.SchemaBank>();
            schema.Load<Fonts.SchemaBank>();
            schema.Load<Textures.SchemaBank>();
            schema.Load<Meshes.SchemaBank>();
            return schema;
        }

        protected override void SetUp()
        {
            base.SetUp();
            simulator.AddSystem<DataImportSystem>();
            simulator.AddSystem<FontImportSystem>();
            simulator.AddSystem<TextRasterizationSystem>();
        }
    }
}

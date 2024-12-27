using Data;
using Data.Components;
using Data.Systems;
using Fonts;
using Fonts.Components;
using Fonts.Systems;
using Meshes;
using Meshes.Components;
using Rendering;
using Rendering.Components;
using Simulation.Tests;
using System.Threading;
using System.Threading.Tasks;
using Textures;
using Textures.Components;
using Worlds;

namespace TextRendering.Systems.Tests
{
    public class TextMeshTests : SimulationTests
    {
        static TextMeshTests()
        {
            TypeLayout.Register<IsMesh>("IsMesh");
            TypeLayout.Register<IsTextMesh>("IsTextMesh");
            TypeLayout.Register<IsTextMeshRequest>("IsTextMeshRequest");
            TypeLayout.Register<IsTextRenderer>("IsTextRenderer");
            TypeLayout.Register<IsFont>("IsFont");
            TypeLayout.Register<IsFontRequest>("IsFontRequest");
            TypeLayout.Register<IsGlyph>("IsGlyph");
            TypeLayout.Register<IsTexture>("IsTexture");
            TypeLayout.Register<IsTextureRequest>("IsTextureRequest");
            TypeLayout.Register<IsDataRequest>("IsDataRequest");
            TypeLayout.Register<IsDataSource>("IsDataSource");
            TypeLayout.Register<IsData>("IsData");
            TypeLayout.Register<FontMetrics>("FontMetrics");
            TypeLayout.Register<FontName>("FontName");
            TypeLayout.Register<BinaryData>("BinaryData");
            TypeLayout.Register<AtlasSprite>("AtlasSprite");
            TypeLayout.Register<Pixel>("Pixel");
            TypeLayout.Register<Kerning>("Kerning");
            TypeLayout.Register<FontGlyph>("FontGlyph");
            TypeLayout.Register<TextCharacter>("TextCharacter");
            TypeLayout.Register<MeshVertexPosition>("MeshVertexPosition");
            TypeLayout.Register<MeshVertexNormal>("MeshVertexNormal");
            TypeLayout.Register<MeshVertexUV>("MeshVertexUV");
            TypeLayout.Register<MeshVertexColor>("MeshVertexColor");
            TypeLayout.Register<MeshVertexTangent>("MeshVertexTangent");
            TypeLayout.Register<MeshVertexBiTangent>("MeshVertexBiTangent");
            TypeLayout.Register<MeshVertexIndex>("MeshVertexIndex");
        }

        protected override void SetUp()
        {
            base.SetUp();
            world.Schema.RegisterComponent<IsMesh>();
            world.Schema.RegisterComponent<IsTextMesh>();
            world.Schema.RegisterComponent<IsTextMeshRequest>();
            world.Schema.RegisterComponent<IsTextRenderer>();
            world.Schema.RegisterComponent<IsFont>();
            world.Schema.RegisterComponent<IsFontRequest>();
            world.Schema.RegisterComponent<IsGlyph>();
            world.Schema.RegisterComponent<IsTexture>();
            world.Schema.RegisterComponent<IsTextureRequest>();
            world.Schema.RegisterComponent<IsDataRequest>();
            world.Schema.RegisterComponent<IsDataSource>();
            world.Schema.RegisterComponent<IsData>();
            world.Schema.RegisterComponent<FontMetrics>();
            world.Schema.RegisterComponent<FontName>();
            world.Schema.RegisterArrayElement<BinaryData>();
            world.Schema.RegisterArrayElement<AtlasSprite>();
            world.Schema.RegisterArrayElement<Pixel>();
            world.Schema.RegisterArrayElement<Kerning>();
            world.Schema.RegisterArrayElement<FontGlyph>();
            world.Schema.RegisterArrayElement<TextCharacter>();
            world.Schema.RegisterArrayElement<MeshVertexPosition>();
            world.Schema.RegisterArrayElement<MeshVertexNormal>();
            world.Schema.RegisterArrayElement<MeshVertexUV>();
            world.Schema.RegisterArrayElement<MeshVertexColor>();
            world.Schema.RegisterArrayElement<MeshVertexTangent>();
            world.Schema.RegisterArrayElement<MeshVertexBiTangent>();
            world.Schema.RegisterArrayElement<MeshVertexIndex>();
            simulator.AddSystem<DataImportSystem>();
            simulator.AddSystem<FontImportSystem>();
            simulator.AddSystem<TextRasterizationSystem>();
        }

        [Test, CancelAfter(4000)]
        public async Task GenerateTextMesh(CancellationToken cancellation)
        {
            EmbeddedAddress.Register(GetType().Assembly, "Assets/Arial.otf");

            string sampleText = "What is up";
            Font arialFont = new(world, "*/Arial.otf");
            TextMesh textMesh = new(world, sampleText, arialFont);
            await textMesh.UntilCompliant(Simulate, cancellation);

            Mesh mesh = textMesh;
            Assert.That(mesh.HasPositions(), Is.True);
            Assert.That(mesh.HasNormals(), Is.False);
            Assert.That(mesh.HasUVs(), Is.True);
            Assert.That(mesh.HasColors(), Is.False);
            Assert.That(mesh.GetVertexPositions().Length, Is.EqualTo(sampleText.Length * 4));
            Assert.That(mesh.GetVertexCount(), Is.EqualTo(sampleText.Length * 4));

            //todo: write asserts to verify the generation with the arial font
        }
    }
}

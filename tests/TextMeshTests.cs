using Data;
using Fonts;
using Meshes;
using Rendering;
using System.Threading;
using System.Threading.Tasks;
using Worlds;

namespace TextRendering.Systems.Tests
{
    public class TextMeshTests : TextRenderingSystemsTests
    {
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

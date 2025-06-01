using Data;
using Fonts;
using Meshes;
using Rendering;
using System.Threading;
using System.Threading.Tasks;

namespace TextRendering.Systems.Tests
{
    public class TextMeshTests : TextRenderingSystemsTests
    {
        [Test, CancelAfter(4000)]
        public async Task GenerateTextMesh(CancellationToken cancellation)
        {
            EmbeddedResourceRegistry.Register(GetType().Assembly, "Assets/Arial.otf");

            string sampleText = "What is up";
            Font arialFont = new(world, "*/Arial.otf");
            TextMesh textMesh = new(world, sampleText, arialFont);
            await textMesh.UntilCompliant(Update, cancellation);

            Mesh mesh = textMesh;
            Assert.That(mesh.ContainsPositions, Is.True);
            Assert.That(mesh.ContainsNormals, Is.False);
            Assert.That(mesh.ContainsUVs, Is.True);
            Assert.That(mesh.VertexCount, Is.EqualTo(sampleText.Length * 4));

            //todo: write asserts to verify the generation with the arial font
        }
    }
}

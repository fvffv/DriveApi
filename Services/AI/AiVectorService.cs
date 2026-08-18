using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
namespace drive_api.Services.AI
{
    public class AiVectorService
    {
        private BertTokenizer _vocab;
        private InferenceSession _visionSession;
        private InferenceSession _textSession;

        private static readonly float[] ImageMean = new[] { 0.48145466f, 0.4578275f, 0.40821073f };
        private static readonly float[] ImageStd = new[] { 0.26862954f, 0.26130258f, 0.27577711f };
        private const int ImageSize = 224;

        public async Task InitModelsAsync(string modelsPath)
        {
            try
            {
                _vocab = new BertTokenizer();
                await _vocab.LoadVocabularyAsync(Path.Combine(modelsPath, "vocab.txt"), true);

                _visionSession = new InferenceSession(Path.Combine(modelsPath, "Vit-L-14.img.fp32.onnx"));
                _textSession = new InferenceSession(Path.Combine(modelsPath, "Vit-L-14.txt.fp32.onnx"));
            }
            catch (Exception e)
            {
                throw new Exception($"AI 模型加载失败: {e.Message}");
            }
        }

        /// <summary>
        /// 提取图片向量
        /// </summary>
        public float[] GetImageFeature(string imagePath)
        {
            using Image<Rgb24> image = Image.Load<Rgb24>(imagePath);


            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(ImageSize, ImageSize),

                // 把原来的 ResizeMode.Crop 改成 ResizeMode.Pad
                Mode = ResizeMode.Crop,

                // 填充颜色通常用纯黑 (或中性灰)
                PadColor = Color.Black
            }));

            // 2. 将像素数据转换为 Tensor 并进行归一化
            var inputTensor = new DenseTensor<float>(new[] { 1, 3, ImageSize, ImageSize });

            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    Span<Rgb24> pixelSpan = accessor.GetRowSpan(y);
                    for (int x = 0; x < accessor.Width; x++)
                    {
                        // 归一化公式: (pixel / 255.0 - mean) / std
                        inputTensor[0, 0, y, x] = ((pixelSpan[x].R / 255f) - ImageMean[0]) / ImageStd[0];
                        inputTensor[0, 1, y, x] = ((pixelSpan[x].G / 255f) - ImageMean[1]) / ImageStd[1];
                        inputTensor[0, 2, y, x] = ((pixelSpan[x].B / 255f) - ImageMean[2]) / ImageStd[2];
                    }
                }
            });

            // 3. 运行图片模型 (注意：不同模型输入的名称可能不同，常见为 "pixel_values" 或 "image")
            // 你可以通过 session.InputMetadata.Keys.First() 动态获取正确的输入层名称
            string inputName = _visionSession.InputMetadata.Keys.First();
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _visionSession.Run(inputs);

            return results.First().AsEnumerable<float>().ToArray();
        }

        /// <summary>
        /// 提取文本特征,适配不同 ONNX 导出版本的变量名
        /// </summary>
        public float[] GetTextFeature(string text)
        {
            int maxLength = 52;

            var (inputIdsMemory, attentionMaskMemory, tokenTypeIdsMemory) = _vocab.Encode(text, maxLength);

            long[] ids = inputIdsMemory.ToArray();
            long[] mask = attentionMaskMemory.ToArray();
            long[] types = tokenTypeIdsMemory.ToArray();

            if (ids.Length < maxLength)
            {
                Array.Resize(ref ids, maxLength);
                Array.Resize(ref mask, maxLength);
                Array.Resize(ref types, maxLength);
            }

            var dimensions = new ReadOnlySpan<int>(new[] { 1, maxLength });
            var inputIds = new DenseTensor<long>(ids, dimensions);
            var attentionMask = new DenseTensor<long>(mask, dimensions);
            var tokenTypeIds = new DenseTensor<long>(types, dimensions);

            // ================= 动态匹配模型的输入名 =================
            var inputs = new List<NamedOnnxValue>();
            var requiredInputs = _textSession.InputMetadata.Keys.ToList(); // 获取模型真正需要的变量名

            // 1. 匹配 ID (可能是 text 或者 input_ids)
            if (requiredInputs.Contains("text"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("text", inputIds));
            else if (requiredInputs.Contains("input_ids"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", inputIds));

            // 2. 匹配 Attention Mask
            if (requiredInputs.Contains("attention_mask"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask));

            // 3. 匹配 Token Type IDs
            if (requiredInputs.Contains("token_type_ids"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));

            // ===================================================================

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _textSession.Run(inputs);

            return results.First().AsEnumerable<float>().ToArray();
        }
    }
}

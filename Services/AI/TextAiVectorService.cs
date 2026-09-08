using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Microsoft.SemanticKernel.Text;
using System.Text;
namespace drive_api.Services.AI
{
    public class TextAiVectorService
    {
        // BGE-M3 最大支持 8192 token。
        // 普通文本建议 512；长文章应先分块。
        private const int DefaultMaximumTokens = 512;

        private readonly object _tokenizerLock = new();

        private SentencePieceTokenizer? _tokenizer;
        private FileStream? _tokenizerStream;
        private InferenceSession? _session;
        public Task InitModelsAsync(string modelsPath)
        {
            try
            {
                string onnxPath = Path.Combine(modelsPath, "bgem3.onnx");
                string SentencePiecePath = Path.Combine(modelsPath, "sentencepiece.bpe.model");
                if (!File.Exists(onnxPath))
                {
                    throw new FileNotFoundException("找不到 ONNX 模型。请下载放置并命名:" + onnxPath, onnxPath);
                }

                if (!File.Exists(SentencePiecePath))
                {
                    throw new FileNotFoundException("找不到 SentencePiece 模型。请下载放置并命名:" + SentencePiecePath, SentencePiecePath);
                }

                _tokenizerStream = File.OpenRead(SentencePiecePath);

                // BGE-M3 使用 SentencePiece Unigram 分词。
                _tokenizer = SentencePieceTokenizer.Create(
                    _tokenizerStream,
                    addBeginningOfSentence: true,
                    addEndOfSentence: true,
                    specialTokens: new Dictionary<string, int>());

                // bgem3.onnx_data 必须和 bgem3.onnx 位于同一目录。
                _session = new InferenceSession(onnxPath);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                throw new Exception($"AI 模型加载失败: {e.Message}");
            }
        }

        /// <summary>
        /// 将文本转换为 BGE-M3 向量。
        /// </summary>
        public float[] GetTextFeature(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException(
                    "文本不能为空。",
                    nameof(text));
            }

            if (_tokenizer is null || _session is null)
            {
                throw new InvalidOperationException(
                    "模型尚未初始化，请先调用 InitModelsAsync。");
            }

            // 自动进行 SentencePiece 分词。
            int[] inputIds;

            lock (_tokenizerLock)
            {
                var sentencePieceIds = _tokenizer.EncodeToIds(
                    text,
                    addBeginningOfSentence: true,
                    addEndOfSentence: true,
                    considerPreTokenization: true,
                    considerNormalization: true);

                // 限制最大 token 数。
                inputIds = sentencePieceIds
                    .Take(DefaultMaximumTokens)
                    .Select(MapToBgeM3TokenId)
                    .ToArray();
            }

            if (inputIds.Length == 0)
            {
                throw new InvalidOperationException(
                    "文本分词后没有得到有效 token。");
            }

            // 如果文本过长被截断，最后一个 token 设置为 </s>。
            if (inputIds[^1] != 2)
            {
                inputIds[^1] = 2;
            }

            // 创建 ONNX 输入张量，形状为 [batch, sequence]。
            var inputIdsTensor =
                new DenseTensor<long>(new[] { 1, inputIds.Length });

            var attentionMaskTensor =
                new DenseTensor<long>(new[] { 1, inputIds.Length });

            for (int i = 0; i < inputIds.Length; i++)
            {
                inputIdsTensor[0, i] = inputIds[i];

                // 1 表示有效 token，当前没有 padding。
                attentionMaskTensor[0, i] = 1;
            }

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                _session.Run(new[]
                {
                NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    inputIdsTensor),

                NamedOnnxValue.CreateFromTensor(
                    "attention_mask",
                    attentionMaskTensor)
                });

            // BGE-M3 的句向量输出名称是 sentence_embedding。
            float[] vector = results
                .First(x => x.Name == "sentence_embedding")
                .AsTensor<float>()
                .ToArray();

            // 归一化后可以直接使用点积计算相似度。
            Normalize(vector);

            return vector;
        }

        /// <summary>
        /// 从 UTF-8 文本文件读取内容并生成向量。
        /// </summary>
        public IReadOnlyList<TextVectorChunk> GetTextFeatureFromFile(string textPath)
        {
            if (!File.Exists(textPath))
            {
                throw new FileNotFoundException(
                    "找不到文本文件。",
                    textPath);
            }
         
            string text = File.ReadAllText(textPath);

            // 分割文本块
            List<string> chunks = SplitTextToChunks(
              text,
              maxTokens: DefaultMaximumTokens,
              overlapTokens: 64);

            return chunks
                .Select((chunk, index) => new TextVectorChunk(
                    Index: index,
                    Vector: GetTextFeature(chunk))) // 每一块各自产生一个 float[1024]
                .ToList();
        }
        /// <summary>
        /// 从文本读取内容并生成向量。
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public IReadOnlyList<TextVectorChunk> GetTextFeatureFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
               return [];
            }

            // 分割文本块
            List<string> chunks = SplitTextToChunks(
              text,
              maxTokens: DefaultMaximumTokens,
              overlapTokens: 64);

            return chunks
                .Select((chunk, index) => new TextVectorChunk(
                    Index: index,
                    Vector: GetTextFeature(chunk))) // 每一块各自产生一个 float[1024]
                .ToList();
        }

        /// <summary>
        /// 计算文本实际的 BGE-M3 token 数。
        /// </summary>
        public int CountTokens(string text)
        {
            if (_tokenizer is null)
            {
                throw new InvalidOperationException(
                    "模型尚未初始化。");
            }

            lock (_tokenizerLock)
            {
                return _tokenizer.EncodeToIds(
                    text,
                    addBeginningOfSentence: true,
                    addEndOfSentence: true,
                    considerPreTokenization: true,
                    considerNormalization: true).Count;
            }
        }

        /// <summary>
        /// 将 SentencePiece 模型的 token ID 映射为 BGE-M3/XLM-R 的 token ID。
        /// </summary>
        private static int MapToBgeM3TokenId(int id)
        {
            return id switch
            {
                0 => 3,               // SentencePiece <unk> -> BGE-M3 <unk>
                1 => 0,               // SentencePiece <s>   -> BGE-M3 <s>
                2 => 2,               // SentencePiece </s>  -> BGE-M3 </s>
                _ => id + 1           // 普通 token 整体后移一位
            };
        }

        /// <summary>
        /// 对向量进行 L2 归一化。
        /// </summary>
        private static void Normalize(float[] vector)
        {
            float sum = 0;

            foreach (float value in vector)
            {
                sum += value * value;
            }

            float norm = MathF.Sqrt(sum);

            if (norm == 0)
            {
                return;
            }

            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
        /// <summary>
        /// 将长文本按 BGE-M3 的真实 token 数分块。
        /// </summary>
        private List<string> SplitTextToChunks(string text, int maxTokens, int overlapTokens)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            if (overlapTokens >= maxTokens)
            {
                throw new ArgumentException(
                    "overlapTokens 必须小于 maxTokens。");
            }

            // 先按换行和标点拆成较短的文本行。
#pragma warning disable SKEXP0050 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
            var lines = TextChunker.SplitPlainTextLines(
                text,
                maxTokensPerLine: maxTokens,
                tokenCounter: CountTokens);
#pragma warning restore SKEXP0050 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。

            // 再组合成最终文本块，并保留重叠 token。
#pragma warning disable SKEXP0050 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
            return TextChunker.SplitPlainTextParagraphs(
                lines,
                maxTokensPerParagraph: maxTokens,
                overlapTokens: overlapTokens,
                tokenCounter: CountTokens);
#pragma warning restore SKEXP0050 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
        }

    }


    /// <summary>
    /// 文本分割后的向量块
    /// </summary>
    /// <param name="Index">顺序</param>
    /// <param name="Vector">向量</param>
    public record TextVectorChunk(int Index, float[] Vector);
}

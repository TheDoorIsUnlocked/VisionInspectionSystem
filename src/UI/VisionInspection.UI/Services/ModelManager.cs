using Microsoft.Data.Sqlite;
using Microsoft.ML.OnnxRuntime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 模型扫描结果
    /// </summary>
    public class ModelScanResult
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public ModelType DetectedType { get; set; } = ModelType.ObjectDetection;
        public List<string> Classes { get; set; } = new();
        public int InputSize { get; set; } = 640;
        public bool IsAlreadyInDatabase { get; set; } = false;
        public string? ExistingModelId { get; set; }
    }

    /// <summary>
    /// 模型管理器实现
    /// </summary>
    public class ModelManager : IModelManager
    {
        private readonly string _dbPath;
        private readonly string _modelsDirectory;
        private readonly string _yoloModelsDirectory;
        private ModelInfo? _currentModel;

        public ModelInfo? CurrentModel => _currentModel;
        public bool IsModelLoaded => _currentModel != null;

        public event EventHandler<bool>? ModelLoadedStateChanged;
        public event EventHandler? ModelListChanged;

        public ModelManager()
        {
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "models.db");
            _modelsDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
            
            // 使用项目根目录下的yolo_models文件夹（绝对路径）
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // 从 bin\Debug\net8.0-windows 回退到项目根目录
            var projectRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
            _yoloModelsDirectory = Path.Combine(projectRoot, "yolo_models");

            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            Directory.CreateDirectory(_modelsDirectory);
            Directory.CreateDirectory(_yoloModelsDirectory);

            InitializeDatabase();
        }

        /// <summary>
        /// 获取YOLO模型文件夹路径
        /// </summary>
        public string YoloModelsDirectory => _yoloModelsDirectory;

        /// <summary>
        /// 扫描模型文件夹中的ONNX模型
        /// </summary>
        public async Task<List<ModelScanResult>> ScanModelsDirectoryAsync(string? directoryPath = null)
        {
            return await Task.Run(() =>
            {
                var results = new List<ModelScanResult>();
                var targetDirectory = directoryPath ?? _yoloModelsDirectory;

                if (!Directory.Exists(targetDirectory))
                {
                    return results;
                }

                // 获取所有ONNX文件
                var onnxFiles = Directory.GetFiles(targetDirectory, "*.onnx", SearchOption.AllDirectories);
                var existingModels = GetAllModelsAsync().Result;
                var existingPaths = existingModels.ToDictionary(m => m.ModelPath, m => m.Id);

                foreach (var filePath in onnxFiles)
                {
                    try
                    {
                        var result = AnalyzeModelFile(filePath);
                        
                        // 检查是否已在数据库中
                        if (existingPaths.ContainsKey(filePath))
                        {
                            result.IsAlreadyInDatabase = true;
                            result.ExistingModelId = existingPaths[filePath];
                        }

                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"分析模型文件失败 {filePath}: {ex.Message}");
                    }
                }

                return results;
            });
        }

        /// <summary>
        /// 分析模型文件，自动识别类型
        /// </summary>
        private ModelScanResult AnalyzeModelFile(string filePath)
        {
            var result = new ModelScanResult
            {
                FilePath = filePath,
                FileName = Path.GetFileNameWithoutExtension(filePath)
            };

            try
            {
                // 使用ONNX Runtime读取模型信息
                using var session = new InferenceSession(filePath);
                
                // 获取输入输出信息
                var inputMetadata = session.InputMetadata;
                var outputMetadata = session.OutputMetadata;
                var outputNames = outputMetadata.Keys.Select(k => k.ToLower()).ToList();
                var outputShapes = outputMetadata.Values.Select(v => v.Dimensions.Select(d => (long)d).ToArray()).ToList();

                // 根据文件名关键词识别类型
                var fileNameLower = result.FileName.ToLower();
                
                // 根据输出维度识别模型类型
                result.DetectedType = DetectModelType(outputShapes, outputNames, fileNameLower);

                // 尝试从模型元数据获取类别信息
                var classNames = ExtractClassNames(session);
                if (classNames.Count > 0)
                {
                    result.Classes = classNames;
                }
                else
                {
                    // 根据文件名推测类别
                    result.Classes = InferClassesFromFileName(fileNameLower);
                }

                // 获取输入尺寸
                if (inputMetadata.Count > 0)
                {
                    var firstInput = inputMetadata.First().Value;
                    var dimensions = firstInput.Dimensions;
                    if (dimensions.Length >= 3)
                    {
                        // 通常是 [batch, channels, height, width] 或 [batch, height, width, channels]
                        var dim2 = dimensions[2];
                        var dim3 = dimensions[3];
                        result.InputSize = Math.Max(dim2 == -1 ? 640 : dim2, dim3 == -1 ? 640 : dim3);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解析ONNX模型失败: {ex.Message}");
                // 根据文件名进行基本识别
                result.DetectedType = InferTypeFromFileName(result.FileName.ToLower());
                result.Classes = InferClassesFromFileName(result.FileName.ToLower());
            }

            return result;
        }

        /// <summary>
        /// 根据输出形状检测模型类型
        /// </summary>
        private ModelType DetectModelType(List<long[]> outputShapes, List<string> outputNames, string fileNameLower)
        {
            // 根据文件名关键词
            if (fileNameLower.Contains("seg") || fileNameLower.Contains("segment"))
                return ModelType.Segmentation;
            if (fileNameLower.Contains("cls") || fileNameLower.Contains("classif"))
                return ModelType.Classification;
            if (fileNameLower.Contains("pose") || fileNameLower.Contains("keypoint"))
                return ModelType.PoseEstimation;
            if (fileNameLower.Contains("obb") || fileNameLower.Contains("oriented"))
                return ModelType.OBBDetection;

            // 根据输出形状
            if (outputShapes.Count >= 2)
            {
                // 分割模型通常有两个输出：检测输出 + 掩码输出
                var lastOutput = outputShapes.Last();
                if (lastOutput.Length >= 3 && lastOutput[^1] > 100)  // 掩码输出通常有较大维度
                    return ModelType.Segmentation;
            }

            // 分类模型通常只有一个输出，维度较小
            if (outputShapes.Count == 1 && outputShapes[0].Length == 2)
                return ModelType.Classification;

            // 姿态估计模型输出通常包含关键点坐标
            if (outputNames.Any(n => n.Contains("keypoint") || n.Contains("pose")))
                return ModelType.PoseEstimation;

            // 默认为目标检测
            return ModelType.ObjectDetection;
        }

        /// <summary>
        /// 从文件名推断模型类型
        /// </summary>
        private ModelType InferTypeFromFileName(string fileNameLower)
        {
            if (fileNameLower.Contains("seg"))
                return ModelType.Segmentation;
            if (fileNameLower.Contains("cls") || fileNameLower.Contains("class"))
                return ModelType.Classification;
            if (fileNameLower.Contains("pose"))
                return ModelType.PoseEstimation;
            if (fileNameLower.Contains("obb"))
                return ModelType.OBBDetection;
            return ModelType.ObjectDetection;
        }

        /// <summary>
        /// 从模型中提取类别名称
        /// </summary>
        private List<string> ExtractClassNames(InferenceSession session)
        {
            var classes = new List<string>();
            
            try
            {
                // 尝试从模型元数据获取类别信息
                var metadata = session.ModelMetadata.CustomMetadataMap;
                foreach (var kvp in metadata)
                {
                    var key = kvp.Key.ToLower();
                    if (key.Contains("names") || 
                        key.Contains("classes") ||
                        key.Contains("labels"))
                    {
                        var value = kvp.Value;
                        // 尝试解析JSON格式的类别列表
                        if (value.StartsWith("{") && value.EndsWith("}"))
                        {
                            try
                            {
                                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
                                if (dict != null)
                                {
                                    classes = dict.Values.ToList();
                                    return classes;
                                }
                            }
                            catch { }
                        }
                        
                        // 尝试解析逗号分隔的列表
                        classes = value.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                        if (classes.Count > 0)
                            return classes;
                    }
                }
            }
            catch { }

            return classes;
        }

        /// <summary>
        /// 从文件名推断类别
        /// </summary>
        private List<string> InferClassesFromFileName(string fileNameLower)
        {
            // 常见模型类型的默认类别
            if (fileNameLower.Contains("coco"))
            {
                return new List<string> { "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
                    "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
                    "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
                    "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket", "bottle",
                    "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple", "sandwich", "orange",
                    "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch", "potted plant", "bed",
                    "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone", "microwave", "oven",
                    "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush" };
            }
            
            if (fileNameLower.Contains("sop"))
            {
                return new List<string> { "hand", "hand_holding", "hand_empty", "screwdriver", "wrench", "glue_gun", 
                    "tool_other", "part_base", "part_cover", "part_screw", "assembly_ok", "zone_active" };
            }

            // 默认返回空列表，后续可以通过加载模型获取
            return new List<string>();
        }

        /// <summary>
        /// 批量导入扫描到的模型
        /// </summary>
        public async Task<int> ImportScannedModelsAsync(List<ModelScanResult> modelsToImport)
        {
            int importedCount = 0;

            foreach (var scanResult in modelsToImport.Where(m => !m.IsAlreadyInDatabase))
            {
                var modelInfo = new ModelInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = scanResult.FileName,
                    Description = $"自动导入的模型\n文件: {scanResult.FileName}",
                    ModelPath = scanResult.FilePath,
                    Type = scanResult.DetectedType,
                    Classes = scanResult.Classes,
                    InputSize = scanResult.InputSize,
                    ConfidenceThreshold = 0.5f,
                    IouThreshold = 0.45f,
                    SmoothEma = 0.2f,
                    SmoothConfirmHits = 2,
                    SmoothMaxMissed = 5,
                    SmoothIouThreshold = 0.3f,
                    UseGpu = true,
                    GpuId = 0
                };

                if (await AddModelAsync(modelInfo))
                {
                    importedCount++;
                }
            }

            return importedCount;
        }

        private void InitializeDatabase()
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            string createTableSql = @"
                CREATE TABLE IF NOT EXISTS Models (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT,
                    ModelPath TEXT NOT NULL,
                    Type INTEGER NOT NULL,
                    Classes TEXT,
                    InputSize INTEGER DEFAULT 640,
                    ConfidenceThreshold REAL DEFAULT 0.5,
                    IouThreshold REAL DEFAULT 0.45,
                    SmoothEma REAL DEFAULT 0.2,
                    SmoothConfirmHits INTEGER DEFAULT 2,
                    SmoothMaxMissed INTEGER DEFAULT 5,
                    SmoothIouThreshold REAL DEFAULT 0.3,
                    UseGpu INTEGER DEFAULT 1,
                    GpuId INTEGER DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )";

            using var command = new SqliteCommand(createTableSql, connection);
            command.ExecuteNonQuery();

            // 兼容旧数据库：添加平滑参数列
            EnsureColumnExists(connection, "SmoothEma", "REAL DEFAULT 0.2");
            EnsureColumnExists(connection, "SmoothConfirmHits", "INTEGER DEFAULT 2");
            EnsureColumnExists(connection, "SmoothMaxMissed", "INTEGER DEFAULT 5");
            EnsureColumnExists(connection, "SmoothIouThreshold", "REAL DEFAULT 0.3");
        }

        private void EnsureColumnExists(SqliteConnection connection, string columnName, string columnDef)
        {
            using var command = new SqliteCommand(
                "SELECT COUNT(*) FROM pragma_table_info('Models') WHERE name = @name", connection);
            command.Parameters.AddWithValue("@name", columnName);
            var count = (long)(command.ExecuteScalar() ?? 0L);
            if (count == 0)
            {
                using var alter = new SqliteCommand($"ALTER TABLE Models ADD COLUMN {columnName} {columnDef}", connection);
                alter.ExecuteNonQuery();
            }
        }

        public async Task<List<ModelInfo>> GetAllModelsAsync()
        {
            var models = new List<ModelInfo>();

            await Task.Run(() =>
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                string selectSql = "SELECT * FROM Models ORDER BY UpdatedAt DESC";
                using var command = new SqliteCommand(selectSql, connection);
                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    var model = new ModelInfo
                    {
                        Id = reader.GetString(reader.GetOrdinal("Id")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? "" : reader.GetString(reader.GetOrdinal("Description")),
                        ModelPath = reader.GetString(reader.GetOrdinal("ModelPath")),
                        Type = (ModelType)reader.GetInt32(reader.GetOrdinal("Type")),
                        Classes = reader.IsDBNull(reader.GetOrdinal("Classes"))
                            ? new List<string>()
                            : JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("Classes"))) ?? new List<string>(),
                        InputSize = reader.GetInt32(reader.GetOrdinal("InputSize")),
                        ConfidenceThreshold = (float)reader.GetDouble(reader.GetOrdinal("ConfidenceThreshold")),
                        IouThreshold = (float)reader.GetDouble(reader.GetOrdinal("IouThreshold")),
                        SmoothEma = reader.IsDBNull(reader.GetOrdinal("SmoothEma")) ? 0.2f : (float)reader.GetDouble(reader.GetOrdinal("SmoothEma")),
                        SmoothConfirmHits = reader.IsDBNull(reader.GetOrdinal("SmoothConfirmHits")) ? 2 : reader.GetInt32(reader.GetOrdinal("SmoothConfirmHits")),
                        SmoothMaxMissed = reader.IsDBNull(reader.GetOrdinal("SmoothMaxMissed")) ? 5 : reader.GetInt32(reader.GetOrdinal("SmoothMaxMissed")),
                        SmoothIouThreshold = reader.IsDBNull(reader.GetOrdinal("SmoothIouThreshold")) ? 0.3f : (float)reader.GetDouble(reader.GetOrdinal("SmoothIouThreshold")),
                        UseGpu = reader.GetInt32(reader.GetOrdinal("UseGpu")) == 1,
                        GpuId = reader.GetInt32(reader.GetOrdinal("GpuId")),
                        CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
                        UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
                    };
                    models.Add(model);
                }
            });

            return models;
        }

        public async Task<ModelInfo?> GetModelAsync(string id)
        {
            return await Task.Run(() =>
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                string selectSql = "SELECT * FROM Models WHERE Id = @Id";
                using var command = new SqliteCommand(selectSql, connection);
                command.Parameters.AddWithValue("@Id", id);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return new ModelInfo
                    {
                        Id = reader.GetString(reader.GetOrdinal("Id")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? "" : reader.GetString(reader.GetOrdinal("Description")),
                        ModelPath = reader.GetString(reader.GetOrdinal("ModelPath")),
                        Type = (ModelType)reader.GetInt32(reader.GetOrdinal("Type")),
                        Classes = reader.IsDBNull(reader.GetOrdinal("Classes"))
                            ? new List<string>()
                            : JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("Classes"))) ?? new List<string>(),
                        InputSize = reader.GetInt32(reader.GetOrdinal("InputSize")),
                        ConfidenceThreshold = (float)reader.GetDouble(reader.GetOrdinal("ConfidenceThreshold")),
                        IouThreshold = (float)reader.GetDouble(reader.GetOrdinal("IouThreshold")),
                        SmoothEma = reader.IsDBNull(reader.GetOrdinal("SmoothEma")) ? 0.2f : (float)reader.GetDouble(reader.GetOrdinal("SmoothEma")),
                        SmoothConfirmHits = reader.IsDBNull(reader.GetOrdinal("SmoothConfirmHits")) ? 2 : reader.GetInt32(reader.GetOrdinal("SmoothConfirmHits")),
                        SmoothMaxMissed = reader.IsDBNull(reader.GetOrdinal("SmoothMaxMissed")) ? 5 : reader.GetInt32(reader.GetOrdinal("SmoothMaxMissed")),
                        SmoothIouThreshold = reader.IsDBNull(reader.GetOrdinal("SmoothIouThreshold")) ? 0.3f : (float)reader.GetDouble(reader.GetOrdinal("SmoothIouThreshold")),
                        UseGpu = reader.GetInt32(reader.GetOrdinal("UseGpu")) == 1,
                        GpuId = reader.GetInt32(reader.GetOrdinal("GpuId")),
                        CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
                        UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("UpdatedAt")))
                    };
                }

                return null;
            });
        }

        public async Task<bool> AddModelAsync(ModelInfo model)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var connection = new SqliteConnection($"Data Source={_dbPath}");
                    connection.Open();

                    string insertSql = @"
                        INSERT INTO Models (Id, Name, Description, ModelPath, Type, Classes, InputSize, 
                            ConfidenceThreshold, IouThreshold, SmoothEma, SmoothConfirmHits, SmoothMaxMissed, SmoothIouThreshold, UseGpu, GpuId, CreatedAt, UpdatedAt)
                        VALUES (@Id, @Name, @Description, @ModelPath, @Type, @Classes, @InputSize,
                            @ConfidenceThreshold, @IouThreshold, @SmoothEma, @SmoothConfirmHits, @SmoothMaxMissed, @SmoothIouThreshold, @UseGpu, @GpuId, @CreatedAt, @UpdatedAt)";

                    using var command = new SqliteCommand(insertSql, connection);
                    command.Parameters.AddWithValue("@Id", model.Id);
                    command.Parameters.AddWithValue("@Name", model.Name);
                    command.Parameters.AddWithValue("@Description", model.Description);
                    command.Parameters.AddWithValue("@ModelPath", model.ModelPath);
                    command.Parameters.AddWithValue("@Type", (int)model.Type);
                    command.Parameters.AddWithValue("@Classes", JsonSerializer.Serialize(model.Classes));
                    command.Parameters.AddWithValue("@InputSize", model.InputSize);
                    command.Parameters.AddWithValue("@ConfidenceThreshold", model.ConfidenceThreshold);
                    command.Parameters.AddWithValue("@IouThreshold", model.IouThreshold);
                    command.Parameters.AddWithValue("@SmoothEma", model.SmoothEma);
                    command.Parameters.AddWithValue("@SmoothConfirmHits", model.SmoothConfirmHits);
                    command.Parameters.AddWithValue("@SmoothMaxMissed", model.SmoothMaxMissed);
                    command.Parameters.AddWithValue("@SmoothIouThreshold", model.SmoothIouThreshold);
                    command.Parameters.AddWithValue("@UseGpu", model.UseGpu ? 1 : 0);
                    command.Parameters.AddWithValue("@GpuId", model.GpuId);
                    command.Parameters.AddWithValue("@CreatedAt", model.CreatedAt.ToString("O"));
                    command.Parameters.AddWithValue("@UpdatedAt", model.UpdatedAt.ToString("O"));

                    command.ExecuteNonQuery();

                    ModelListChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"添加模型失败: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> UpdateModelAsync(ModelInfo model)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var connection = new SqliteConnection($"Data Source={_dbPath}");
                    connection.Open();

                    string updateSql = @"
                        UPDATE Models SET
                            Name = @Name,
                            Description = @Description,
                            ModelPath = @ModelPath,
                            Type = @Type,
                            Classes = @Classes,
                            InputSize = @InputSize,
                            ConfidenceThreshold = @ConfidenceThreshold,
                            IouThreshold = @IouThreshold,
                            SmoothEma = @SmoothEma,
                            SmoothConfirmHits = @SmoothConfirmHits,
                            SmoothMaxMissed = @SmoothMaxMissed,
                            SmoothIouThreshold = @SmoothIouThreshold,
                            UseGpu = @UseGpu,
                            GpuId = @GpuId,
                            UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var command = new SqliteCommand(updateSql, connection);
                    command.Parameters.AddWithValue("@Id", model.Id);
                    command.Parameters.AddWithValue("@Name", model.Name);
                    command.Parameters.AddWithValue("@Description", model.Description);
                    command.Parameters.AddWithValue("@ModelPath", model.ModelPath);
                    command.Parameters.AddWithValue("@Type", (int)model.Type);
                    command.Parameters.AddWithValue("@Classes", JsonSerializer.Serialize(model.Classes));
                    command.Parameters.AddWithValue("@InputSize", model.InputSize);
                    command.Parameters.AddWithValue("@ConfidenceThreshold", model.ConfidenceThreshold);
                    command.Parameters.AddWithValue("@IouThreshold", model.IouThreshold);
                    command.Parameters.AddWithValue("@SmoothEma", model.SmoothEma);
                    command.Parameters.AddWithValue("@SmoothConfirmHits", model.SmoothConfirmHits);
                    command.Parameters.AddWithValue("@SmoothMaxMissed", model.SmoothMaxMissed);
                    command.Parameters.AddWithValue("@SmoothIouThreshold", model.SmoothIouThreshold);
                    command.Parameters.AddWithValue("@UseGpu", model.UseGpu ? 1 : 0);
                    command.Parameters.AddWithValue("@GpuId", model.GpuId);
                    command.Parameters.AddWithValue("@UpdatedAt", DateTime.Now.ToString("O"));

                    int rowsAffected = command.ExecuteNonQuery();

                    if (rowsAffected > 0)
                    {
                        ModelListChanged?.Invoke(this, EventArgs.Empty);
                        return true;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"更新模型失败: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> DeleteModelAsync(string id)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // 如果当前加载的是这个模型，先卸载
                    if (_currentModel?.Id == id)
                    {
                        UnloadModelAsync().Wait();
                    }

                    using var connection = new SqliteConnection($"Data Source={_dbPath}");
                    connection.Open();

                    string deleteSql = "DELETE FROM Models WHERE Id = @Id";
                    using var command = new SqliteCommand(deleteSql, connection);
                    command.Parameters.AddWithValue("@Id", id);

                    int rowsAffected = command.ExecuteNonQuery();

                    if (rowsAffected > 0)
                    {
                        ModelListChanged?.Invoke(this, EventArgs.Empty);
                        return true;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"删除模型失败: {ex.Message}");
                    return false;
                }
            });
        }

        public async Task<bool> LoadModelAsync(string id)
        {
            var model = await GetModelAsync(id);
            if (model == null)
            {
                return false;
            }

            _currentModel = model;
            ModelLoadedStateChanged?.Invoke(this, true);
            return true;
        }

        public async Task<bool> UnloadModelAsync()
        {
            return await Task.Run(() =>
            {
                _currentModel = null;
                ModelLoadedStateChanged?.Invoke(this, false);
                return true;
            });
        }
    }
}

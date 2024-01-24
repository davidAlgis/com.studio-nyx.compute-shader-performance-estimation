using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using Debug = UnityEngine.Debug;
using UnityEngine;
using UnityEngine.Profiling;

public class GPUExecutionTimeEstimatorWindow : EditorWindow
{
    private class HierarchyItemFrameData
    {
        public int frameIndex;
        public string itemName;
        public string itemPath;
        public int columnCalls;
        public float columnSelfTimeGpu;
        public float columnTotalTimeGpu;
    }

    // The estimator will read for line with the following path. 
    // You can use the find name functions to search for the correct name line.
    private string _linePath = "";

    // The estimator will read for line with the following name. For example if you want to estimate a 
    // kernel execution you should use : UnityEngine.CoreModule.dll!UnityEngine::ComputeShader.Dispatch()
    // You can use the find name functions to search for the correct name line.
    // Warning do not use ComputeShader.Dispatch() it seems that it's associated to the children line of
    // the kernel execution and therefore its duration is 0 ms.
    private string _lineName = "UnityEngine.CoreModule.dll!UnityEngine::ComputeShader.Dispatch()";

    // Frame count corresponds to the number of frame that will be save and read in profiler. 
    // For time estimation it corresponds of the number of sample you want to evaluate your time.
    // Warning, do not increase this variable to much for big project or you will ran out of memory issue.
    private int _frameCount;

    // The string associated to the input field of frame count
    private string _frameCountStr = "50";

    // For time estimation it corresponds to the number of sample you want to evaluate your time.
    private int _totalNbrSample;

    // The string associated to the input field of total number of sample
    private string _totalNbrSampleStr = "200";

    // Count the number of frame
    private int _currentFrame;

    // Count the number of sample
    private int _currentSample;

    // Contains the execution time of the variable
    private readonly List<float> _executionTimes = new();

    // Name of the log file
    private string _logName;

    // List used in profiler data fetching 
    private readonly List<int> _parentsCacheList = new();
    private readonly List<int> _childrenCacheList = new();

    // for a unknown reason the column index of gpu time in HierarchyFrameDataView is set to internal. Therefore we have
    // to hardcode its index manually...
    private const int _IndexColumnSelfTimeGpu = 8;
    private const int _IndexColumnTotalTimeGpu = 9;

    // For big project the default max memory for profiler is not enough, therefore we need to increase it.
    // Be careful to not use greater value than int max. The default value used by Unity is 64 Mb.
    // The unit of this variable is byte.
    private const int _DefaultMaxUsedMemory = 640000000;

    // the string associated to the input field
    private string _maxUsedMemory = _DefaultMaxUsedMemory.ToString();

    // Csv folder where the data will be exported
    private string _csvPathName = "C:\\estimation.csv";

    // If true will export data to a csv a _csvPath
    private bool _exportCsv;

    // The button display log will show only line that contains the following path
    private string _linePathContains;

    // The button display log will show only line that contains the following name.
    // For example if you are looking for kernel execution you should use : UnityEngine.CoreModule.dll!UnityEngine::ComputeShader.Dispatch()
    // You can use the find name functions to search for the correct name line.
    // Warning do not use ComputeShader.Dispatch() it seems that it's associated to the children line of
    // the kernel execution and therefore its duration is 0 ms.
    private string _lineNameContains = "UnityEngine.CoreModule.dll!UnityEngine::ComputeShader.Dispatch()";

    // Check if the class is already processing ! 
    private bool _isProcessing;

    // to estimate the remaining time we defined a start time
    private float _startTime;

    private bool _isFirstFrame;

    [MenuItem("Tools/GPU Execution Time Estimator")]
    public static void ShowWindow()
    {
        GetWindow<GPUExecutionTimeEstimatorWindow>("GPU Execution Time Estimator");
    }

    private void OnGUI()
    {
        GUIStyle guiStyleTitle = new(GUI.skin.label)
        {
            fontSize = 18,
            normal =
            {
                textColor = new Color(0.82f, 0.82f, 0.82f)
            },
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };

        if (EditorApplication.isPlaying == false && ProfilerDriver.deepProfiling == false)
        {
            ProfilerDriver.deepProfiling = true;
            EditorUtility.RequestScriptReload();
        }

        GUILayout.Space(5f);
        GUILayout.Label("Estimate Execution Time", guiStyleTitle);
        GUILayout.Space(10f);

        EditorGUILayout.HelpBox(
            "In play mode, after having opened the profiler, click on Estimate Execution Times button to generate " +
            _totalNbrSampleStr + " sample from profiler data " + _frameCountStr + " by " + _frameCountStr +
            ". It will read and them and estimate the execution time of the line indicates by the line path and line name.",
            MessageType.Info);
        GUILayout.Space(10f);

        _linePath = EditorGUILayout.TextField(
            new GUIContent("Line Path in profiler",
                "The estimator will read for line that contains the following path. " +
                "Path is defined by all the namespace/functions parents of the call we want to estimate." +
                "You can use the find name functions to search for the correct name line."),
            _linePath);

        _lineName = EditorGUILayout.TextField(
            new GUIContent("Line Name in profiler",
                "The estimator will read for line that contains following name. For example if you want to estimate a" +
                " kernel execution you should use : UnityEngine.CoreModule.dll!UnityEngine::ComputeShader.Dispatch() " +
                "Warning do not use ComputeShader.Dispatch() it seems that it's associated to the children line of the" +
                " kernel execution and therefore its duration is 0 ms."),
            _lineName);
        _totalNbrSampleStr =
            EditorGUILayout.TextField(
                new GUIContent("Total number of sample",
                    "For time estimation it corresponds to the number of sample you want to evaluate your time. " +
                    "For time estimation it corresponds of the number of sample you want to evaluate your time. " +
                    "Warning, do not increase this variable to much for big project or you will ran out of memory issue."),
                _totalNbrSampleStr);
        _frameCountStr =
            EditorGUILayout.TextField(
                new GUIContent("Frame Count",
                    "Frame count corresponds to the number of frame that will be save and read in profiler. " +
                    "As log file becomes bigger and bigger, the number of frame is bound by the memory consumption of " +
                    "our project, which can be far bellow the total number of sample. Therefore, we need to save and read " +
                    "small frame count and then delete the log file and re-save and re-read another small frame count... until " +
                    "we read the total number of sample."),
                _frameCountStr);
        _maxUsedMemory =
            EditorGUILayout.TextField(
                new GUIContent("Max Used Memory Profiler", "For big project the default max memory for" +
                                                           " profiler is not enough, therefore we need to increase it." +
                                                           " Be careful to not use greater value than int max. The default " +
                                                           "value used by Unity is 64 Mb. The unit of this variable is byte."),
                _maxUsedMemory);
        GUILayout.BeginHorizontal();
        _exportCsv = GUILayout.Toggle(_exportCsv, "Export data to csv to");
        if (_exportCsv)
        {
            _csvPathName =
                EditorGUILayout.TextField(
                    _csvPathName);
        }

        GUILayout.EndHorizontal();
        EditorGUI.BeginDisabledGroup(_isProcessing);
        if (GUILayout.Button("Estimate Execution Times"))
        {
            EstimateExecutionTime();
        }

        EditorGUI.EndDisabledGroup();

        GUILayout.Space(10);

        GUILayout.Space(15);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        GUILayout.Space(15);

        GUILayout.Label("Display Log Lines : ", guiStyleTitle);
        GUILayout.Space(10);

        EditorGUILayout.HelpBox(
            "To find the path and the name of the line you want to estimate, you can click, in play, after having opened the profiler, on the" +
            " button Display Log Lines Log for one frame to generate the profiler data for only one frame and " +
            "to show all lines that contains the path and the name bellow.",
            MessageType.Info);
        GUILayout.Space(10f);
        _linePathContains =
            EditorGUILayout.TextField(
                new GUIContent("Display lines path with",
                    "The button display log will show only line that contains the following path. " +
                    "Path is defined by all the namespace/functions parents of the call we want to estimate."),
                _linePathContains);
        _lineNameContains =
            EditorGUILayout.TextField(
                new GUIContent("Display lines name with",
                    "The button display log will show only line that contains the following name. " +
                    "For example if you are looking for kernel execution you should use : " +
                    "UnityEngine.CoreModule.dll!UnityEngine::ComputeShader.Dispatch(). You can use the find name" +
                    " functions to search for the correct name line. Warning do not use ComputeShader.Dispatch() " +
                    "it seems that it's associated to the children line of the kernel execution and " +
                    "therefore its duration is 0 ms."),
                _lineNameContains);
        EditorGUI.BeginDisabledGroup(_isProcessing);
        if (GUILayout.Button("Display Log Lines"))
        {
            LogProfilerLines();
        }

        EditorGUI.EndDisabledGroup();
    }

    private void EstimateExecutionTime()
    {
        if (EditorApplication.isPlaying == false)
        {
            EditorUtility.DisplayDialog("Error",
                "Estimation must be launched in Play Mode.", "OK");
            return;
        }

        if (string.IsNullOrEmpty(_linePath))
        {
            EditorUtility.DisplayDialog("Error", "Line name of profiler cannot be empty", "OK");
            return;
        }

        if (_maxUsedMemory == "")
        {
            Profiler.maxUsedMemory = _DefaultMaxUsedMemory;
        }
        else
        {
            if (int.TryParse(_maxUsedMemory, out int maxUsedMemory) == false)
            {
                EditorUtility.DisplayDialog("Error",
                    "Invalid max used memory : " + _maxUsedMemory + " it cannot be parse in int", "OK");
                return;
            }

            if (maxUsedMemory < 0)
            {
                EditorUtility.DisplayDialog("Error",
                    "Invalid max used memory : " + _maxUsedMemory + " it cannot be negative", "OK");
                return;
            }

            Profiler.maxUsedMemory = maxUsedMemory;
            if (Profiler.maxUsedMemory != _DefaultMaxUsedMemory)
            {
                Debug.Log("Profiler max used memory is set to : " + maxUsedMemory);
            }
        }

        if (!int.TryParse(_frameCountStr, out _frameCount) || _frameCount <= 0)
        {
            EditorUtility.DisplayDialog("Error", "Invalid frame count", "OK");
            return;
        }

        if (!int.TryParse(_totalNbrSampleStr, out _totalNbrSample) || _totalNbrSample <= 0)
        {
            EditorUtility.DisplayDialog("Error", "Invalid total number of sample !", "OK");
            return;
        }

        _currentFrame = 0;
        _executionTimes.Clear();
        _isProcessing = true;
        _isFirstFrame = true;
        EditorApplication.update += UpdateLog;
        EditorApplication.update += UpdateEstimationTime;
    }

    private void LogProfilerLines()
    {
        if (EditorApplication.isPlaying == false)
        {
            EditorUtility.DisplayDialog("Error",
                "log profiler lines must be launched in Play Mode.", "OK");
            return;
        }

        Profiler.maxUsedMemory = _DefaultMaxUsedMemory;

        _currentFrame = 0;
        _frameCount = 1;
        _isProcessing = true;

        EditorApplication.update += UpdateLog;
        EditorApplication.update += UpdateDisplayLog;
    }

    private void UpdateLog()
    {
        if (_isFirstFrame)
        {
            _startTime = Time.realtimeSinceStartup;
            _isFirstFrame = false;
        }

        if (_currentFrame == 0)
        {
            BeginLogProfiler();
        }

        if (_currentFrame == _frameCount)
        {
            EditorApplication.update -= UpdateLog;
            EndLogProfiler();
        }

        _currentFrame++;
    }

    private void UpdateEstimationTime()
    {
        if (_currentFrame > _frameCount)
        {
            List<List<HierarchyItemFrameData>> capturedFramesData = ProcessLogProfiler(_linePath, _lineName);
            if (capturedFramesData == null)
            {
                EditorApplication.update -= UpdateEstimationTime;
                return;
            }

            ComputeInfoItem(capturedFramesData);

            if (_executionTimes.Count == 0)
            {
                EditorApplication.update -= UpdateEstimationTime;
                _isProcessing = false;
                Debug.LogError(
                    "There has been an error while getting the estimation time. It seems there are only 0 in time estimation.");
                return;
            }

            if (_executionTimes.Count >= _totalNbrSample)
            {
                EditorApplication.update -= UpdateEstimationTime;
                ShowTimeEstimation();
                _isProcessing = false;
                return;
            }

            _currentFrame = 0;
            EditorApplication.update += UpdateLog;

            // Calculate the time elapsed and estimate remaining time
            float elapsedTime = Time.realtimeSinceStartup - _startTime;
            float averageTimePerFrame = elapsedTime / _executionTimes.Count;
            float remainingTime = averageTimePerFrame * (_totalNbrSample - _executionTimes.Count);
            int remainingMinutes = (int)((remainingTime % 3600) / 60);
            int remainingSeconds = (int)(remainingTime % 60);
            int remainingPercentage = _executionTimes.Count * 100 / _totalNbrSample;

            Debug.Log(
                $"Process sample {_executionTimes.Count}/{_totalNbrSample} ({remainingPercentage}%) - Estimated remaining time: {remainingMinutes:00} min {remainingSeconds:00} sec");
        }
    }

    private void UpdateDisplayLog()
    {
        if (_currentFrame > _frameCount)
        {
            EditorApplication.update -= UpdateDisplayLog;
            List<List<HierarchyItemFrameData>> capturedFramesData =
                ProcessLogProfiler(_linePathContains, _lineNameContains);
            if(capturedFramesData != null)
            {
                DisplayLog(capturedFramesData);
            }
            _isProcessing = false;
        }
    }

    /// <summary>
    /// Enabled log 
    /// </summary>
    private void BeginLogProfiler()
    {
        // clear the frame before this to avoid saving useless frame
        ProfilerDriver.ClearAllFrames();
        // disable all area
        Array values = Enum.GetValues(typeof(ProfilerArea));
        foreach (object p in values)
        {
            ProfilerDriver.SetAreaEnabled((ProfilerArea)p, false);
        }

        // enable only GPU area
        ProfilerDriver.SetAreaEnabled(ProfilerArea.GPU, true);
        ProfilerDriver.enabled = true;
    }

    /// <summary>
    /// Save log and close it
    /// </summary>
    private void EndLogProfiler()
    {
        string timeId = DateTime.Now.ToString("s").Replace(":", "_");
        string formattedId = "Log".ToLowerInvariant().Replace(".", "_");
        if (ProfilerDriver.deepProfiling)
        {
            formattedId += "_deep";
        }

        _logName =
            $"Assets/{formattedId}_{timeId}.profile.data".Replace("\\", "/");
        ProfilerDriver.SaveProfile(_logName);
        AssetDatabase.ImportAsset(_logName);

        ProfilerDriver.enabled = false;
    }

    /// <summary>
    /// Will load and process the profiler data saved in _logName, it will look at each frame for line containing the path and name bellow 
    /// </summary>
    /// <param name="linePath">Will only store the line containing this path</param>
    /// <param name="lineName">Will only store the line containing this name</param>
    /// <returns>A list for each frame of a list of lines containing the path and name above</returns>
    private List<List<HierarchyItemFrameData>> ProcessLogProfiler(string linePath, string lineName)
    {
        // load profiler
        if (ProfilerDriver.LoadProfile(_logName, true) == false)
        {
            EditorUtility.DisplayDialog("Error", "Unable to read profile log. Exit !", "OK");
            return null;
        }

        var capturedFramesData = new List<List<HierarchyItemFrameData>>();

        // WARNING : The profiler save some extra frame for a unknown reason, therefore to have
        // the correct number of frame (_frameCount) we impose the first frame to be
        // ProfilerDriver.lastFrameIndex - _frameCount if it's greater than ProfilerDriver.firstFrameIndex
        // We save only the _frameCount last frame (and not the first one) to get the more accurate, the first 
        // could be not the one that wanted to save and moreover in gpu time estimation the first frame are always
        // less accurate than the first one. 
        int first = Mathf.Max(ProfilerDriver.lastFrameIndex - _frameCount, ProfilerDriver.firstFrameIndex);
        int last = ProfilerDriver.lastFrameIndex;
        int nbrFrameSaved = last - first;

        for (int i = first; i < last; i++)
        {
            List<HierarchyItemFrameData> frameData = ProcessHierarchyFrameData(i, linePath, lineName);
            if (frameData == null || frameData.Count < 0)
            {
                Debug.LogError($"Couldn't find any line containing path : {linePath} or name {lineName}. Abort !");
                capturedFramesData = null;
                break;
            }

            capturedFramesData.Add(frameData);
        }

        if (File.Exists(_logName))
        {
            File.Delete(_logName);
        }

        // we delete the meta too
        string logNameMeta = _logName + ".meta";
        if (File.Exists(logNameMeta))
        {
            File.Delete(logNameMeta);
        }

        if (capturedFramesData == null || capturedFramesData.Count < 1)
        {
            Debug.LogError(
                "No item found with these path/name... <b>Maybe click on the profiler or open it if you didn't !</b>");
            return null;
        }

        return capturedFramesData;
    }

    /// <summary>
    /// Process one frame of the profiler data saved to find the line containing a given path and name. 
    /// </summary>
    /// <param name="frame">Frame index to process</param>
    /// <param name="linePath">Will only store the line containing this path</param>
    /// <param name="lineName">Will only store the line containing this name</param>
    /// <returns>A list with line containing the path and name above</returns>
    private List<HierarchyItemFrameData> ProcessHierarchyFrameData(int frame, string linePath, string lineName)
    {
        string linePathNotNull = linePath == null ? "" : linePath;
        string lineNameNotNull = lineName == null ? "" : lineName;
        List<HierarchyItemFrameData> f = new();

        using HierarchyFrameDataView frameData = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
            HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnGcMemory,
            true);
        int rootId = frameData.GetRootItemID();

        frameData.GetItemDescendantsThatHaveChildren(rootId, _parentsCacheList);
        foreach (int parentId in _parentsCacheList)
        {
            frameData.GetItemChildren(parentId, _childrenCacheList);
            foreach (int child in _childrenCacheList)
            {
                string itemPath = frameData.GetItemPath(child);
                string itemName = frameData.GetItemName(child);

                if(linePath != null )
                if (itemPath.Contains(linePathNotNull) &&
                    itemName.Contains(lineNameNotNull))
                {
                    HierarchyItemFrameData h = new()
                    {
                        frameIndex = frame,
                        itemName = itemName,
                        itemPath = itemPath,
                        columnCalls =
                            (int)frameData.GetItemColumnDataAsSingle(child, HierarchyFrameDataView.columnCalls),
                        columnSelfTimeGpu = frameData.GetItemColumnDataAsSingle(child, _IndexColumnSelfTimeGpu),
                        columnTotalTimeGpu = frameData.GetItemColumnDataAsSingle(child, _IndexColumnTotalTimeGpu)
                    };

                    f.Add(h);
                }
            }
        }

        return f;
    }

    /// <summary>
    /// Get the execution time of each frame stored in capturedFramesData
    /// </summary>
    private void ComputeInfoItem(List<List<HierarchyItemFrameData>> capturedFramesData)
    {
        string lastName = "";
        string lastPath = "";
        if (capturedFramesData.Count > 0 && capturedFramesData[0].Count > 0)
        {
            lastName = capturedFramesData[0][0].itemName;
            lastPath = capturedFramesData[0][0].itemPath;
        }

        foreach (List<HierarchyItemFrameData> frameData in capturedFramesData)
        {
            foreach (HierarchyItemFrameData item in frameData)
            {
                // if we have line with different name or path we count only one unique line.
                if (item.itemName != lastName || item.itemPath != lastPath)
                {
                    Debug.LogWarning(
                        "It seems that the name and the path you gave is common to two different line. " +
                        "We will only estimate the line with path : \"" +
                        item.itemPath + "\" and with name :\"" + item.itemName + "\"");
                    continue;
                }

                if (item.columnTotalTimeGpu == 0.0f)
                {
                    continue;
                }

                _executionTimes.Add(item.columnTotalTimeGpu);
            }
        }
    }

    private void ShowTimeEstimation()
    {
        // After collecting data, calculate average and variance
        float average = _executionTimes.Average();
        float variance = _executionTimes.Sum(time => Mathf.Pow(time - average, 2)) / _executionTimes.Count;

        Debug.Log($"Average Time: {average} ms, Variance: {variance} ms");
        if(_exportCsv)
        {
            LastDataToCsv();
        }
    }

    /// <summary>
    /// Display in log the item path and item name  of each frame stored in capturedFramesData
    /// </summary>
    private void DisplayLog(List<List<HierarchyItemFrameData>> capturedFramesData)
    {
        if (capturedFramesData == null)
        {
            Debug.LogError("Unable to display null log");
            return;
        }

        foreach (List<HierarchyItemFrameData> frameData in capturedFramesData)
        {
            foreach (HierarchyItemFrameData item in frameData)
            {
                Debug.Log(
                    $"Item path : \"{item.itemPath}\"\nItem name : \"{item.itemName}\"");
            }
        }
    }

    private void LastDataToCsv()
    {
        int length = _executionTimes.Count;
        try
        {
            using StreamWriter file = new(_csvPathName);
            for (int i = 0; i < length; i++)
            {
                file.Write(i + "; " + _executionTimes[i].ToString(CultureInfo.InvariantCulture));
                file.Write(Environment.NewLine);
            }

            Debug.Log("Saved data at <a file=\"" + _csvPathName + "\">" + _csvPathName + "</a>");
        }
        catch (Exception ex)
        {
            // Catch any exceptions during file writing
            Debug.LogError("Error occurred while saving data to CSV: " + ex.Message);
        }
    }
}

using UnityEngine;
using Unity.MLAgents;
using Unity.InferenceEngine; // REQUIRED for ML-Agents 4.0.3!
using TMPro; // For TextMeshPro UI text

public class DifficultyManager : MonoBehaviour
{
    public static DifficultyManager instance;

    [Header("The AI Configuration")]
    public NewFighter aiFighter;
    public string behaviorName = "FighterBehaviour"; // MUST match the Inspector exactly

    [Header("The Brains (ONNX Files)")]
    // ML-Agents 4.0.3 uses ModelAsset instead of NNModel!
    public ModelAsset[] difficultyLevels; 
    public int currentDifficulty = 1;  // Start the player on Medium (Level 1)

    [Header("UI Display")]
    public TMP_Text difficultyText;        // Drag your UI Text (TMP) here
    public GameObject difficultyLabelRoot; // Optional: the whole label/badge object to show/hide
    public string[] difficultyLabels = { "Easy", "Medium", "Hard" }; // Index-matched to difficultyLevels

    [Header("Tracking")]
    private int playerConsecutiveWins = 0;
    private int aiConsecutiveWins = 0;

    // Automatically derived - true only when aiFighter exists AND is actually flagged as AI.
    // No manual toggling needed: FightManager already sets NewFighter.isAI correctly per match.
    private bool IsAIModeActive => aiFighter != null && aiFighter.isAI;

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        // Set the starting brain when the scene loads
        SetDifficulty(currentDifficulty);
    }

    // FightManager disables this component entirely in Human vs Human mode
    // (DifficultyManager.instance.enabled = false), so make sure the label
    // hides the moment that happens, and refreshes the moment it's re-enabled.
    void OnEnable()
    {
        UpdateDifficultyDisplay(currentDifficulty);
    }

    void OnDisable()
    {
        SetLabelVisible(false);
    }

    public void EvaluatePerformance(bool humanWonRound)
    {
        if (humanWonRound)
        {
            playerConsecutiveWins++;
            aiConsecutiveWins = 0; // Reset AI streak

            if (playerConsecutiveWins >= 1)
            {
                IncreaseDifficulty();
                playerConsecutiveWins = 0;
            }
        }
        else
        {
            aiConsecutiveWins++;
            playerConsecutiveWins = 0; // Reset Human streak

            if (aiConsecutiveWins >= 1)
            {
                DecreaseDifficulty();
                aiConsecutiveWins = 0;
            }
        }

        Debug.Log("<color=yellow>ADAPTIVE AI: Player Wins = " + playerConsecutiveWins + ", AI Wins = " + aiConsecutiveWins);
    }

    private void IncreaseDifficulty()
    {
        if (currentDifficulty < difficultyLevels.Length - 1)
        {
            currentDifficulty++;
            SetDifficulty(currentDifficulty);
        }
    }

    private void DecreaseDifficulty()
    {
        if (currentDifficulty > 0)
        {
            currentDifficulty--;
            SetDifficulty(currentDifficulty);
        }
    }

    public void SetDifficulty(int levelIndex)
    {
        if (aiFighter != null)
        {
            // Safety Net 1: Is the array slot empty?
            if (difficultyLevels[levelIndex] == null)
            {
                Debug.LogError("<color=red>CRASH PREVENTED:</color> The brain file for Level " + levelIndex + " is missing in the Inspector!");
                return; 
            }

            // Clean Swap for ML-Agents 4.0.3
            aiFighter.SetModel(behaviorName, difficultyLevels[levelIndex]);
            Debug.Log("<color=cyan>ADAPTIVE AI: Successfully swapped brain to Level " + levelIndex + "</color>");
        }

        UpdateDifficultyDisplay(levelIndex);
    }

    private void UpdateDifficultyDisplay(int levelIndex)
    {
        bool showLabel = IsAIModeActive;
        SetLabelVisible(showLabel);

        if (!showLabel || difficultyText == null)
        {
            return;
        }

        // Safety net: don't crash if labels array doesn't line up with brains array
        if (levelIndex >= 0 && levelIndex < difficultyLabels.Length)
        {
            difficultyText.text = difficultyLabels[levelIndex];
        }
        else
        {
            difficultyText.text = "Level " + levelIndex;
        }
    }

    private void SetLabelVisible(bool visible)
    {
        if (difficultyLabelRoot != null)
        {
            difficultyLabelRoot.SetActive(visible);
        }
        else if (difficultyText != null)
        {
            difficultyText.gameObject.SetActive(visible);
        }
    }
}
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.VFX;
using UnityEngine.InputSystem;

public enum SoundType { Hit, Whiff, Block, Impact, Break }

public class FightManager : MonoBehaviour
{
    private const float HorizontalOverlapBoxWidth = 0.1f;
    private const float VerticalOverlapBoxHeight = 0.1f;

    public static FightManager instance;
    [SerializeField] private NewFighter[] fighters = new NewFighter[2];
    [SerializeField] private GameObject hitParticlePrefab;
    [SerializeField] private GameObject blockParticlePrefab;
    [SerializeField] private GameObject breakParticlePrefab;
    
    [Header("Game Modes")]
    [SerializeField] private bool trainingMode;
    [SerializeField] public bool mlAgentsTrainingMode; 

    [Header("GUI")]
    [SerializeField] private GameObject guiCanvas;
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private Image[] fighterHealthBars;
    [SerializeField] private GameObject[] fighterRoundIcons;
    [SerializeField] private Animator roundAnimator;
    [SerializeField] private TextMeshProUGUI roundText;
    [SerializeField] private TextMeshProUGUI KOText;
    [SerializeField] private GameObject rematchCanvas;
    [SerializeField] private TextMeshProUGUI winnerText;

    [Header("Pause")]
    [SerializeField] private GameObject pauseCanvas;
    private bool isPaused;

    [Header("Audio")]
    [SerializeField] private AudioClip[] hitSounds;
    [SerializeField] private AudioClip[] whiffSounds;
    [SerializeField] private AudioClip[] blockSounds;
    [SerializeField] private AudioClip[] impactSounds;
    [SerializeField] private AudioClip breakSound;

    private bool hitstopActive;
    private bool roundOver;
    private Coroutine[] regenCoroutines;
    private Coroutine timerCoroutine;
    private int[] roundsWon;
    private int roundNum;

    public NewFighter GetOpponent(NewFighter fighter)
    {
        return fighters[0] == fighter ? fighters[1] : fighters[0];
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        Application.targetFrameRate = 60;
        regenCoroutines = new Coroutine[2];
        roundsWon = new int[2];
        roundNum = 1;

        if (FightLoader.instance == null)
            return;

        fighters[0] = Instantiate(FightLoader.instance.fighterPrefabs[0]).GetComponent<NewFighter>();
        fighters[1] = Instantiate(FightLoader.instance.fighterPrefabs[1]).GetComponent<NewFighter>();

        if (FightLoader.instance.gameMode == GameMode.HumanVsHuman)
        {
            fighters[0].isAI = false;
            fighters[1].isAI = false;

            fighters[0].playerInput.SwitchCurrentControlScheme(
                FightLoader.instance.controlSchemes[0],
                FightLoader.instance.fighterDevices[0]);

            fighters[1].playerInput.SwitchCurrentControlScheme(
                FightLoader.instance.controlSchemes[1],
                FightLoader.instance.fighterDevices[1]);
        }
        else // Human vs AI
        {
            fighters[0].isAI = false;
            fighters[1].isAI = true;

            // Don't touch PlayerInput here.
        }

        ResetRound();
    }

    private void Start()
    {
        if (fighters.Length >= 2 && fighters[0] != null && fighters[1] != null)
        {
            // NOTE: isAI for both fighters is already set correctly in Awake()
            // based on FightLoader.instance.gameMode. We do NOT re-derive it
            // here from MatchData.isPlayerVsAI anymore - that was a leftover
            // from the old mode-selection system and was silently overwriting
            // the correct value that Awake() had just set, which is why
            // fighters[1].isAI kept showing as false in vsAI mode.

            if (fighters[1].isAI)
            {
                // --- VS AI MODE ---
                if (DifficultyManager.instance != null)
                {
                    DifficultyManager.instance.enabled = true;

                    // DifficultyManager.aiFighter is a scene-serialized Inspector
                    // field, so it can never already point at a fighter that is
                    // instantiated at runtime. Wire it up here every time a
                    // match starts, otherwise SetDifficulty() silently no-ops.
                    DifficultyManager.instance.aiFighter = fighters[1];
                    DifficultyManager.instance.SetDifficulty(DifficultyManager.instance.currentDifficulty);
                }
            }
            else
            {
                // --- VS HUMAN MODE ---
                if (DifficultyManager.instance != null)
                {
                    DifficultyManager.instance.enabled = false;
                }
            }
        }

        foreach (NewFighter fighter in fighters)
        {
            fighter.BreakThrow.AddListener(OnBreakThrow);
            fighter.TookDamage.AddListener(OnTookDamage);
        }
    }

    private void Update()
    {
        if (!mlAgentsTrainingMode && !roundOver && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            TogglePause();

        if (!hitstopActive && !isPaused)
        {
            UpdateSides();
            Push();
        }
    }

    public void TogglePause()
    {
        if (roundOver) return; // don't allow pausing over the KO/rematch screens

        isPaused = !isPaused;
        pauseCanvas.SetActive(isPaused);

        foreach (NewFighter fighter in fighters)
        {
            if (isPaused) fighter.PauseFighter();
            else fighter.UnpauseFighter();
        }
    }

    public void OnPauseButtonPressed() => TogglePause();
    public void OnResumeButtonPressed() => TogglePause();

    public void OnRestartButtonPressed()
    {
        isPaused = false;
        pauseCanvas.SetActive(false);
        OnRematchPressed(); // reuses your existing restart logic
    }

    public void OnMainMenuButtonPressed()
    {
        isPaused = false;
        pauseCanvas.SetActive(false);
        OnQuitPressed(); // already loads scene 0
    }

    private void LateUpdate()
    {
        // DO NOT put the 'return' line here! 

        // This block handles human vs human (ignores if ML training)
        if (!trainingMode && !mlAgentsTrainingMode && !roundOver)
        {
            if ((fighters[0].currentHealth > 0 && fighters[1].currentHealth <= 0) || 
                (fighters[0].currentHealth <= 0 && fighters[1].currentHealth > 0) || 
                (fighters[0].currentHealth <= 0 && fighters[1].currentHealth <= 0))
            {
                if (fighters[0].currentHealth <= 0) roundsWon[1] += 1;
                if (fighters[1].currentHealth <= 0) roundsWon[0] += 1;
                roundNum += 1;
                UpdateRoundIcons();

                // Feed the round result to the Difficulty Manager (vsAI only).
                // We only report a CLEAN win/loss - a double K.O. is ambiguous
                // for a difficulty signal, so it's intentionally skipped.
                if (fighters[1].isAI && DifficultyManager.instance != null)
                {
                    bool humanWonRound = fighters[1].currentHealth <= 0 && fighters[0].currentHealth > 0;
                    bool aiWonRound = fighters[0].currentHealth <= 0 && fighters[1].currentHealth > 0;

                    if (humanWonRound) DifficultyManager.instance.EvaluatePerformance(true);
                    else if (aiWonRound) DifficultyManager.instance.EvaluatePerformance(false);
                }

                StopAllCoroutines();
                StartCoroutine(EndRound("K.O."));
            }
        }
        // This block handles the infinite ML-Agents training loop
        else if (mlAgentsTrainingMode && !roundOver) 
        {
            if (fighters[0].currentHealth <= 0 || fighters[1].currentHealth <= 0)
            {
                roundOver = true;

                if (fighters[0].currentHealth > 0 && fighters[0].isAI) fighters[0].AddReward(1.0f);
                if (fighters[0].currentHealth <= 0 && fighters[0].isAI) fighters[0].AddReward(-1.0f);
                
                if (fighters[1].currentHealth > 0 && fighters[1].isAI) fighters[1].AddReward(1.0f);
                if (fighters[1].currentHealth <= 0 && fighters[1].isAI) fighters[1].AddReward(-1.0f);

                if (fighters[0].isAI) fighters[0].EndEpisode();
                if (fighters[1].isAI) fighters[1].EndEpisode();
                
                ResetRound(); // Instantly resets health and positions
            }
        }
    }

    private void UpdateRoundIcons()
    {
        if (roundsWon[0] == 1) fighterRoundIcons[0].SetActive(true);
        else if (roundsWon[0] == 2) fighterRoundIcons[1].SetActive(true);
        if (roundsWon[1] == 1) fighterRoundIcons[2].SetActive(true);
        else if (roundsWon[1] == 2) fighterRoundIcons[3].SetActive(true);
    }

    private void ResetRound()
    {
        StopAllCoroutines();
        for (int i = 0; i < 2; i++)
        {
            fighters[i].UnpauseFighter();
            fighters[i].ResetFighter(i == 0);
            fighterHealthBars[i].fillAmount = 1f;
        }
        foreach (GameObject projectile in GameObject.FindGameObjectsWithTag("ProjectileBox")) Destroy(projectile);
        foreach (GameObject particle in GameObject.FindGameObjectsWithTag("Particles")) Destroy(particle);
        KOText.gameObject.SetActive(false);
        timerText.text = "59";
        hitstopActive = false;
        roundOver = false;
        StartCoroutine(StartRound());
    }

    public void OnRematchPressed() { if (FightLoader.instance != null) FightLoader.instance.LoadStage(); else SceneManager.LoadScene(SceneManager.GetActiveScene().name); }
    public void OnQuitPressed() { SceneManager.LoadScene(0); }

    private IEnumerator StartRound()
    {
        if (!mlAgentsTrainingMode)
        {
            roundText.text = $"Round {roundNum}";
            roundAnimator.Play("RoundStart", -1, 0f);
            roundAnimator.Update(Time.deltaTime);
        }

        foreach (NewFighter fighter in fighters) fighter.PauseFighter(false);

        if (!mlAgentsTrainingMode) yield return new WaitForSeconds(roundAnimator.GetCurrentAnimatorStateInfo(0).length);
        else yield return null; 

        foreach (NewFighter fighter in fighters) fighter.UnpauseFighter();

        if (!mlAgentsTrainingMode) timerCoroutine = StartCoroutine(Timer());
    }

    private IEnumerator EndRound(string text)
    {
        if (mlAgentsTrainingMode)
        {
            ResetRound();
            yield break;
        }
        roundOver = true;
        for (int i = 0; i < 4; i++) yield return null;
        foreach (NewFighter fighter in fighters) fighter.PauseFighter();
        foreach (GameObject projectile in GameObject.FindGameObjectsWithTag("ProjectileBox")) projectile.GetComponent<Projectile>().Pause();
        foreach (GameObject particle in GameObject.FindGameObjectsWithTag("Particles"))
        {
            particle.GetComponent<DespawnAfterTime>().StopAllCoroutines();
            particle.GetComponent<VisualEffect>().pause = true;
        }

        KOText.text = text;
        if (text == "K.O.") KOText.fontSize = 505;
        else if (text == "Time Over") KOText.fontSize = 254;
        KOText.gameObject.SetActive(true);

        yield return new WaitForSeconds(1f);

        if (roundsWon[0] == 2 && roundsWon[1] < 2) { KOText.text = ""; rematchCanvas.SetActive(true); winnerText.text = "Player 1 Wins!"; }
        else if (roundsWon[0] < 2 && roundsWon[1] == 2) { KOText.text = ""; rematchCanvas.SetActive(true); winnerText.text = "Player 2 Wins!"; }
        else if (roundsWon[0] == 2 && roundsWon[1] == 2) { KOText.text = ""; rematchCanvas.SetActive(true); winnerText.text = "Draw!"; }
        else
        {
            roundAnimator.Play("FadeOut", -1, 0f);
            roundAnimator.Update(Time.deltaTime);
            yield return new WaitForSeconds(roundAnimator.GetCurrentAnimatorStateInfo(0).length);
            ResetRound();
        }
    }

   private IEnumerator Timer()
    {
        int timeRemaining = 59;

        while (timeRemaining > 0)
        {
            for (int i = 0; i < 60; i++)
            {
                while (isPaused) yield return null; // hold the countdown while paused
                yield return null;
            }

            timeRemaining -= 1;
            timerText.text = timeRemaining.ToString();
        }

        if (fighters[0].currentHealth >= fighters[1].currentHealth)
        {
            roundsWon[0] += 1;
        }
        
        if (fighters[0].currentHealth <= fighters[1].currentHealth)
        {
            roundsWon[1] += 1;
        }

        roundNum += 1;
        UpdateRoundIcons();
        StartCoroutine(EndRound("Time Over"));
    }

    public IEnumerator ShakeCamera(int duration, float strength)
    {
        if (mlAgentsTrainingMode) yield break; 
        
        Vector3 startingPos = Camera.main.transform.localPosition;
        int elapsedFrames = 0;
        
        while (elapsedFrames < duration)
        {
            Camera.main.transform.localPosition = new Vector3(Random.Range(-1f, 1f) * strength, Random.Range(-1f, 1f) * strength, startingPos.z);
            elapsedFrames += 1;
            yield return null;
        }
        
        Camera.main.transform.localPosition = startingPos;
    }

    private IEnumerator RegenHealth(int fighterIndex) { for (int i = 0; i < 60; i++) yield return null; fighters[fighterIndex].currentHealth = fighters[fighterIndex].maxHealth; fighterHealthBars[fighterIndex].fillAmount = 1f; }

    private IEnumerator Hitstop(int numOfFrames)
    {
        hitstopActive = true;
        foreach (NewFighter fighter in fighters) fighter.PauseFighter();
        for (int i = 0; i < numOfFrames; i++) yield return null;
        hitstopActive = false;
        foreach (NewFighter fighter in fighters) fighter.UnpauseFighter();
    }

    public void OnFighterHit(NewFighter hitFighter, HitData hitData, bool attackWasBlocked)
    {
        NewFighter attacker = GetOpponent(hitFighter);
        
        if (attacker.isAI) 
        {
            attacker.AddReward(attackWasBlocked ? 0.01f : 0.05f * hitData.action.damage);
        }

        Vector3 side = hitFighter.IsOnLeftSide ? Vector3.left : Vector3.right;
        if (hitData.action.type != ActionData.Type.Projectile)
        {
            RaycastHit2D wallHit = Physics2D.Raycast(hitFighter.boxCollider.bounds.center + side * hitFighter.boxCollider.bounds.extents.x * 0.95f, side, hitData.action.pushback);
            if (wallHit) hitData.hitbox.transform.parent.GetComponent<NewFighter>().controller.Move(side * -1f * hitData.action.pushback);
            else hitFighter.controller.Move(side * hitData.action.pushback);
        }
        else hitFighter.controller.Move(side * hitData.action.pushback);

        if (!mlAgentsTrainingMode)
        {
            Vector3 particlePos = hitFighter.boxCollider.bounds.center - side * hitFighter.boxCollider.bounds.extents.x;
            particlePos.y = hitData.hitbox.boxCollider.bounds.center.y;
            if (attackWasBlocked)
            {
                Instantiate(blockParticlePrefab, particlePos, Quaternion.Euler(0f, hitFighter.IsOnLeftSide ? -66f : 66f, 0f));
                PlaySound(SoundType.Block, hitFighter.audioSource);
            }
            else
            {
                Instantiate(hitParticlePrefab, particlePos, Quaternion.identity);
                PlaySound(SoundType.Hit, hitFighter.audioSource);
            }

            StartCoroutine(Hitstop(3));
            if (hitData.action.hitAnim == ActionData.HitAnim.Light || attackWasBlocked) StartCoroutine(ShakeCamera(5, 0.015f));
            else StartCoroutine(ShakeCamera(5, 0.03f));
        }
    }

    private void OnTookDamage(NewFighter fighter)
    {
        if (fighter == fighters[0]) fighterHealthBars[0].fillAmount = Mathf.Max((float)fighter.currentHealth / fighter.maxHealth, 0f);
        else if (fighter == fighters[1]) fighterHealthBars[1].fillAmount = Mathf.Max((float)fighter.currentHealth / fighter.maxHealth, 0f);
    }

    public void OnBreakThrow(NewFighter fighter, NewFighter opponent)
    {
        PlaySound(SoundType.Break, fighter.audioSource);

        float offset = fighter.boxCollider.bounds.extents.x + opponent.boxCollider.bounds.extents.x;
        fighter.controller.Move(Vector3.right * (fighter.IsOnLeftSide ? -offset : offset));

        fighter.controller.Move(Vector3.right * (fighter.IsOnLeftSide ? -1.5f : 1.5f));
        fighter.animator.Play("Base Layer.HitLight", -1, 0f);
        fighter.SwitchState(new Stunned(fighter, 20));

        opponent.controller.Move(Vector3.right * (opponent.IsOnLeftSide ? -1.5f : 1.5f));
        opponent.animator.Play("Base Layer.HitLight", -1, 0f);
        opponent.SwitchState(new Stunned(opponent, 20));

        Vector3 particlePos = (fighter.transform.position + opponent.transform.position) / 2f;
        Instantiate(breakParticlePrefab, particlePos, Quaternion.identity);

        fighter.ClearHitThisFrame();
        opponent.ClearHitThisFrame();
    }

    public void PlaySound(SoundType type, AudioSource source)
    {
        switch (type)
        {
            case SoundType.Hit:
                source.PlayOneShot(hitSounds[Random.Range(0, hitSounds.Length)]);
                break;
            case SoundType.Whiff:
                source.PlayOneShot(whiffSounds[Random.Range(0, whiffSounds.Length)]);
                break;
            case SoundType.Block:
                source.PlayOneShot(blockSounds[Random.Range(0, blockSounds.Length)]);
                break;
            case SoundType.Impact:
                source.PlayOneShot(impactSounds[Random.Range(0, impactSounds.Length)]);
                break;
            case SoundType.Break:
                source.PlayOneShot(breakSound);
                break;
        }
    }

    public void ThrowFighter(NewFighter fighter, NewFighter opponent, ActionData action) { }
    private void UpdateSides()
    {
        if (fighters.Length != 2)
            return;

        if (fighters[0].transform.position.x - fighters[0].boxCollider.bounds.extents.x + 0.015f > fighters[1].transform.position.x)
        {
            if (fighters[0].currentState is Walking)
                fighters[0].SwitchSide(false, true);
            if (fighters[1].currentState is Walking)
                fighters[1].SwitchSide(true, true);
        }
        else if (fighters[0].transform.position.x + fighters[0].boxCollider.bounds.extents.x - 0.015f < fighters[1].transform.position.x)
        {
            if (fighters[0].currentState is Walking)
                fighters[0].SwitchSide(true, true);
            if (fighters[1].currentState is Walking)
                fighters[1].SwitchSide(false, true);
        }
    }    
    private void Push()
    {
        
    }
}
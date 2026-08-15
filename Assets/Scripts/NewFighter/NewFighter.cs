using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

[RequireComponent(typeof(Controller2D))]
[RequireComponent(typeof(PlayerInput))]
[RequireComponent(typeof(AudioSource))]
public class NewFighter : Agent 
{
    public const int BackLayer = 7;
    public const int FrontLayer = 8;

    [HideInInspector] public UnityEvent<NewFighter, NewFighter> BreakThrow;
    [HideInInspector] public UnityEvent<NewFighter> TookDamage;
    private bool paused;
    public Controller2D controller { get; private set; }
    public PlayerInput playerInput { get; private set; }
    public BoxCollider2D boxCollider { get; private set; }
    public Animator animator { get; private set; }
    public AudioSource audioSource { get; private set; }
    public int currentHealth { get; set; }
    public Vector3 velocity { get; set; }
    public bool onGround { get; private set; }
    public bool shouldKnockdown { get; set; }
    public ActionData currentAction { get; set; }
    public bool beingThrown { get; set; }
    public ThrowData currentThrow { get; set; }
    public NewFighter throwOpponent { get; set; }
    public int currentFrame { get; set; }
    public bool actionHasHit { get; set; }
    public bool blocking { get; set; }
    public bool canSpawnProjectile { get; set; }
    [field: SerializeField] public List<NewCollisionBox> currentHitboxes { get; private set; }
    [field: SerializeField] public List<NewCollisionBox> currentHurtboxes { get; private set; }

    [Header("Components & GameObjects")]
    public GameObject model;
    [Header("Fighter Attributes")]
    public Vector3 startingPosition;
    public int maxHealth = 100;
    public float forwardWalkSpeed = 5f;
    public float backwardWalkSpeed = 5f;
    public float horizontalJumpSpeed = 8f;
    public float verticalJumpSpeed = 3f;
    public float gravity = 50f;
    public CollisionBox standingHurtbox;
    [field: SerializeField] public bool IsOnLeftSide { get; private set; }
    
    [Header("Universal Actions")]
    [SerializeField] public ActionData throwBreakAction;

    [Header("Fighter Actions")]
    [SerializeField] public List<ActionData> actions;

    [Header("AI Control")]
    public bool isAI = false;

    private InputInterpreter interpreter;
    public FighterState currentState;
    private InputData currentInput;
    private HitData hitThisFrame;

    private float previousDistance = 0f;    

    public override void Initialize()
    {
        controller = GetComponent<Controller2D>();
        playerInput = GetComponent<PlayerInput>();
        boxCollider = GetComponent<BoxCollider2D>();
        animator = model.GetComponent<Animator>();
        audioSource = GetComponent<AudioSource>();
        currentHealth = maxHealth;
        velocity = Vector3.zero;
        canSpawnProjectile = true;
        interpreter = new InputInterpreter(actions);

        if (playerInput.defaultControlScheme == "KeyboardWASD" || playerInput.defaultControlScheme == "KeyboardArrows")
        {
            playerInput.SwitchCurrentControlScheme(playerInput.defaultControlScheme, Keyboard.current);
        }

        if (!IsOnLeftSide)
        {
            model.transform.localScale = new Vector3(model.transform.localScale.x, model.transform.localScale.y, -model.transform.localScale.z);
        }
    }

    private void Start() => SwitchState(new Walking(this));

    private void Update()
    {
        if (!paused)
        {
            onGround = Physics2D.Raycast(boxCollider.bounds.center, Vector2.down, boxCollider.bounds.extents.y + 0.015f, LayerMask.GetMask("Default"));

            if (!isAI)
            {
                currentInput = InputHelper.GetInputData(playerInput, IsOnLeftSide);
                ProcessInputBuffer(currentInput);
            }
            else
            {
                RequestDecision(); 
            }

            controller.Move(velocity * 0.0167f);
        }
    }

    private void ProcessInputBuffer(InputData inputData)
    {
        SwitchAction(interpreter.UpdateBuffer(inputData));
        currentState.Update(inputData);
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        NewFighter opponent = FightManager.instance.GetOpponent(this);
        if (opponent == null) return;

        sensor.AddObservation((float)currentHealth / maxHealth);
        sensor.AddObservation((float)opponent.currentHealth / opponent.maxHealth);
        sensor.AddObservation((opponent.transform.position.x - transform.position.x) / 10f); 
        sensor.AddObservation((opponent.transform.position.y - transform.position.y) / 5f);
        sensor.AddObservation(onGround ? 1f : 0f);
        sensor.AddObservation(opponent.onGround ? 1f : 0f);
        sensor.AddObservation(transform.position.x / 10f);
        // momentum tracking
        sensor.AddObservation(opponent.velocity.x / 15f);
        sensor.AddObservation(opponent.velocity.y / 15f);
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (!isAI)
            return;

        int movementAction = actions.DiscreteActions[0];
        int combatAction = actions.DiscreteActions[1];

        InputData aiInput = new InputData();

        switch (movementAction)
        {
            case 0:
                aiInput.direction = 5; // Neutral
                break;

            case 1:
                aiInput.direction = IsOnLeftSide ? 4 : 6; // Back
                break;

            case 2:
                aiInput.direction = IsOnLeftSide ? 6 : 4; // Forward
                break;

            case 3:
                aiInput.direction = 2; // Down
                break;

            case 4:
                aiInput.direction = 8; // Up
                break;
        }

        switch (combatAction)
        {
            case 1:
                aiInput.aPressed = true;
                break;

            case 2:
                aiInput.bPressed = true;
                break;

            case 3:
                aiInput.cPressed = true;
                break;

            case 4:
                aiInput.jumpPressed = true;
                break;    
        }

        currentInput = aiInput;
        ProcessInputBuffer(currentInput);

        NewFighter opponent = FightManager.instance.GetOpponent(this);

        if (opponent != null)
        {
            float currentDistance = Mathf.Abs(transform.position.x - opponent.transform.position.x);

            // Initialize on first frame
            if (previousDistance == 0)
                previousDistance = currentDistance;

            // Reward moving closer
            if (currentDistance < previousDistance && currentDistance > 1.5f)
            {
                AddReward(0.001f);
            }

            previousDistance = currentDistance;
        }
        
    }

    private void LateUpdate()
    {
        if (hitThisFrame != null)
        {
            if (hitThisFrame.action.type == ActionData.Type.Grab)
            {
                if (actionHasHit && currentAction != null && currentAction.type == ActionData.Type.Grab)
                {
                    FightManager.instance.OnBreakThrow(this, hitThisFrame.hitbox.transform.parent.GetComponent<NewFighter>());
                }
                else if (!actionHasHit || (currentAction != null && currentAction.type < ActionData.Type.Grab))
                {
                    if (currentState is Walking || currentState is Attacking)
                        FightManager.instance.ThrowFighter(this, hitThisFrame.hitbox.transform.parent.GetComponent<NewFighter>(), hitThisFrame.action);
                }
            }
            else if (!actionHasHit || (currentAction != null && currentAction.type <= hitThisFrame.action.type))
            {
                FightManager.instance.OnFighterHit(this, hitThisFrame, blocking);
                GetHit(hitThisFrame.action, hitThisFrame.hitStun, hitThisFrame.blockStun);
            }
            hitThisFrame = null;
        }
    }

    public void ResetFighter(bool startOnLeftSide)
    {
        StopAllCoroutines(); 
        currentHealth = maxHealth; 
        StartCoroutine(ResetRoutine(startOnLeftSide));
    }

    private IEnumerator ResetRoutine(bool startOnLeftSide)
    {
        yield return null; 

        velocity = Vector3.zero;
        shouldKnockdown = false;
        currentAction = null;
        beingThrown = false;
        currentThrow = null;
        throwOpponent = null;
        currentFrame = 0;
        actionHasHit = false;
        blocking = false;
        canSpawnProjectile = true;

        gameObject.layer = 6;
        model.layer = startOnLeftSide ? FrontLayer : BackLayer;

        float x = startOnLeftSide ? startingPosition.x : -startingPosition.x;
        transform.position = new Vector3(x, startingPosition.y, startingPosition.z);

        interpreter.ClearBuffer();

        SwitchState(new Walking(this));
        animator.Play("Base Layer.Idle", -1, 0f);
    }

    public void SwitchState(FighterState nextState)
    {
        if (currentState != null) currentState.OnStateExit();
        currentState = nextState;
        if (currentState != null)
        {
            gameObject.name = $"Fighter - {currentState.GetType().Name}";
            currentState.OnStateEnter();
        }
    }

    public void SwitchSide(bool nowOnLeftSide, bool delayedFlip)
    {
        if (IsOnLeftSide != nowOnLeftSide)
        {
            IsOnLeftSide = nowOnLeftSide;
            SetModelLayer(nowOnLeftSide ? FrontLayer : BackLayer);
            if (delayedFlip) StartCoroutine(DelayedModelFlip());
            else model.transform.localScale = new Vector3(model.transform.localScale.x, model.transform.localScale.y, -model.transform.localScale.z);
        }
    }

    private IEnumerator DelayedModelFlip()
    {
        yield return null;
        model.transform.localScale = new Vector3(model.transform.localScale.x, model.transform.localScale.y, -model.transform.localScale.z);
    }

    public void SwitchAction(List<ActionData> potentialActions)
    {
        if (potentialActions == null || potentialActions.Count == 0) return;
        foreach (ActionData nextAction in potentialActions)
        {
            if ((currentState is Walking && !nextAction.airOkay) || (currentState is Jumping && velocity.y <= 10f && nextAction.airOkay))
            {
                if (currentAction == null || currentAction.alwaysCancelable)
                {
                    if (nextAction.projectiles.Length == 0 || canSpawnProjectile)
                    {
                        currentAction = nextAction;
                        SwitchState(new Attacking(this));
                        return;
                    }
                }
            }
            else if (currentState is Attacking && currentAction != null)
            {
                foreach (CancelsData data in currentAction.cancels)
                {
                    if (actionHasHit && currentFrame >= data.startEndFrames.x && currentFrame <= data.startEndFrames.y && Array.IndexOf(data.actions, nextAction.actionName) != -1)
                    {
                        if (nextAction.projectiles.Length == 0 || canSpawnProjectile)
                        {
                            currentAction = nextAction;
                            SwitchState(new Attacking(this));
                            return;
                        }
                    }
                }
            }
        }
    }

    public void GetHit(ActionData action, int hitStunFrames, int blockStunFrames)
    {
        if (!blocking)
        {
            currentHealth -= action.damage;
            TookDamage.Invoke(this);
            
            if (isAI) AddReward(-0.05f * action.damage); 
            
            if (onGround)
            {
                if (action.knockdown) { animator.Play("Base Layer.Knockdown", -1, 0f); SwitchState(new Knockdown(this)); }
                else { animator.Play(action.hitAnim == ActionData.HitAnim.Light ? "Base Layer.HitLight" : "Base Layer.HitHeavy", -1, 0f); SwitchState(new Stunned(this, hitStunFrames)); }
            }
            else { if (action.knockdown) shouldKnockdown = true; animator.Play(action.knockdown ? "Base Layer.AirKnockdown" : "Base Layer.HitAir", -1, 0f); SwitchState(new Stunned(this, hitStunFrames)); }
        }
        else 
        { 
            animator.Play("Base Layer.Block", -1, 0f); 
            SwitchState(new Stunned(this, blockStunFrames)); 
        }
    }

    public void SpawnProjectile(ProjectileData data) { canSpawnProjectile = false; Vector3 offset = new Vector3(data.offset.x * (IsOnLeftSide ? 1 : -1), data.offset.y, 0f); Quaternion rotation = Quaternion.Euler(0f, (IsOnLeftSide ? 0f : 180f), 0f); Projectile projectile = Instantiate(data.projectilePrefab, transform.position + offset, rotation).GetComponent<Projectile>(); projectile.ProjectileDestroyed.AddListener(OnProjectileDespawned); projectile.Init(this); }
    public void OnProjectileDespawned(Projectile p) => canSpawnProjectile = true;
    public void OnHitboxCollides(Collider2D col) { if (col.gameObject.tag == "FighterBox" && col.transform.parent.GetComponent<NewFighter>() != this) actionHasHit = true; }
    
    public void OnHurtboxCollides(Collider2D col)
    {
        if (col.tag == "FighterBox")
        {
            NewFighter owner = col.transform.parent.GetComponent<NewFighter>();
            if (owner != this) hitThisFrame = new HitData(owner.currentAction, col.GetComponent<NewCollisionBox>(), owner.currentAction.numberOfFrames - owner.currentFrame + owner.currentAction.hitAdv, owner.currentAction.numberOfFrames - owner.currentFrame + owner.currentAction.blockAdv);
        }
        else if (col.tag == "ProjectileBox")
        {
            Projectile projectile = col.GetComponent<Projectile>();
            if (projectile.owner != this) hitThisFrame = new HitData(projectile.action, col.GetComponent<NewCollisionBox>(), projectile.action.hitAdv, projectile.action.blockAdv);
        }
    }

    public void ClearHitboxes() { foreach (var hb in currentHitboxes) hb.gameObject.SetActive(false); }
    public void ClearHurtboxes() { foreach (var hb in currentHurtboxes) hb.gameObject.SetActive(false); }
    public void ClearHitThisFrame() => hitThisFrame = null;
    public NewCollisionBox GetAvailableHitbox() { foreach (NewCollisionBox hitbox in currentHitboxes) { if (!hitbox.gameObject.activeInHierarchy) return hitbox; } return null; }
    public bool HasActiveHitboxes() { foreach (NewCollisionBox hitbox in currentHitboxes) { if (hitbox.gameObject.activeInHierarchy) return true; } return false; }
    public NewCollisionBox GetAvailableHurtbox() { foreach (NewCollisionBox hurtbox in currentHurtboxes) { if (!hurtbox.gameObject.activeInHierarchy) return hurtbox; } return null; }
    public bool CanBreakThrow() => interpreter.BufferContainsAction(throwBreakAction);
    public void SetModelLayer(int layer) { model.layer = layer; foreach (Transform child in model.transform) child.gameObject.layer = layer; }
    public void PauseFighter(bool pauseAnimator = true) { paused = true; if (pauseAnimator) animator.enabled = false; }
    public void UnpauseFighter() { paused = false; animator.enabled = true; }
    public void GetThrown(Transform opponent, ActionData throwAction) { beingThrown = true; currentThrow = throwAction.throwData; throwOpponent = opponent.GetComponent<NewFighter>(); SwitchState(new Throwing(this)); controller.Move(opponent.position - transform.position); }
    private IEnumerator TranslateAfterFrame(Vector3 translation) { yield return null; controller.Move(translation); }
}
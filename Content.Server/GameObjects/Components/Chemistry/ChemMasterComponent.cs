﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.GameObjects.Components.GUI;
using Content.Server.GameObjects.Components.Items.Storage;
using Content.Server.GameObjects.Components.Power.ApcNetComponents;
using Content.Server.GameObjects.EntitySystems;
using Content.Server.Interfaces;
using Content.Server.Interfaces.GameObjects.Components.Items;
using Content.Shared.Chemistry;
using Content.Shared.GameObjects.Components.Chemistry.ChemMaster;
using Content.Shared.GameObjects.EntitySystems;
using Content.Shared.Interfaces.GameObjects.Components;
using Robust.Server.GameObjects.Components.Container;
using Robust.Server.GameObjects.Components.UserInterface;
using Robust.Server.GameObjects.EntitySystems;
using Robust.Server.Interfaces.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.GameObjects.Systems;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Interfaces.Random;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Maths;
using Robust.Shared.Random;
using Robust.Shared.Serialization;
using Robust.Shared.ViewVariables;

namespace Content.Server.GameObjects.Components.Chemistry
{
    /// <summary>
    /// Contains all the server-side logic for chem masters. See also <see cref="SharedChemMasterComponent"/>.
    /// This includes initializing the component based on prototype data, and sending and receiving messages from the client.
    /// Messages sent to the client are used to update update the user interface for a component instance.
    /// Messages sent from the client are used to handle ui button presses.
    /// </summary>
    [RegisterComponent]
    [ComponentReference(typeof(IActivate))]
    [ComponentReference(typeof(IInteractUsing))]
    public class ChemMasterComponent : SharedChemMasterComponent, IActivate, IInteractUsing, ISolutionChange
    {
#pragma warning disable 649
        [Dependency] private readonly IServerNotifyManager _notifyManager;
        [Dependency] private readonly ILocalizationManager _localizationManager;
#pragma warning restore 649

        [ViewVariables] private BoundUserInterface _userInterface;
        [ViewVariables] private ContainerSlot _beakerContainer;
        [ViewVariables] private string _packPrototypeId;

        [ViewVariables] private bool HasBeaker => _beakerContainer.ContainedEntity != null;

        [ViewVariables] private bool BufferModeTransfer = true;

        private PowerReceiverComponent _powerReceiver;
        private bool Powered => _powerReceiver.Powered;

        private readonly SolutionComponent BufferSolution = new SolutionComponent();


        /// <summary>
        /// Shows the serializer how to save/load this components yaml prototype.
        /// </summary>
        /// <param name="serializer">Yaml serializer</param>
        public override void ExposeData(ObjectSerializer serializer)
        {
            base.ExposeData(serializer);

            serializer.DataField(ref _packPrototypeId, "pack", string.Empty);
        }

        /// <summary>
        /// Called once per instance of this component. Gets references to any other components needed
        /// by this component and initializes it's UI and other data.
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();
            _userInterface = Owner.GetComponent<ServerUserInterfaceComponent>()
                .GetBoundUserInterface(ChemMasterUiKey.Key);
            _userInterface.OnReceiveMessage += OnUiReceiveMessage;

            _beakerContainer =
                ContainerManagerComponent.Ensure<ContainerSlot>($"{Name}-reagentContainerContainer", Owner);
            _powerReceiver = Owner.GetComponent<PowerReceiverComponent>();
            _powerReceiver.OnPowerStateChanged += OnPowerChanged;

            //BufferSolution = Owner.BufferSolution
            BufferSolution.Solution = new Solution();
            BufferSolution.MaxVolume = ReagentUnit.New(1000);

            UpdateUserInterface();
        }

        private void OnPowerChanged(object sender, PowerStateEventArgs e)
        {
            UpdateUserInterface();
        }

        /// <summary>
        /// Handles ui messages from the client. For things such as button presses
        /// which interact with the world and require server action.
        /// </summary>
        /// <param name="obj">A user interface message from the client.</param>
        private void OnUiReceiveMessage(ServerBoundUserInterfaceMessage obj)
        {
            var msg = (UiActionMessage) obj.Message;
            var needsPower = msg.action switch
            {
                UiAction.Eject => false,
                _ => true,
            };

            if (!PlayerCanUseChemMaster(obj.Session.AttachedEntity, needsPower))
                return;

            switch (msg.action)
            {
                case UiAction.Eject:
                    TryEject(obj.Session.AttachedEntity);
                    break;
                case UiAction.ChemButton:
                    TransferReagent(msg.id, msg.amount, msg.isBuffer);
                    break;
                case UiAction.Transfer:
                    BufferModeTransfer = true;
                    UpdateUserInterface();
                    break;
                case UiAction.Discard:
                    BufferModeTransfer = false;
                    UpdateUserInterface();
                    break;
                case UiAction.CreatePills:
                case UiAction.CreateBottles:
                    TryCreatePackage(obj.Session.AttachedEntity, msg.action, msg.pillAmount, msg.bottleAmount);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            ClickSound();
        }

        /// <summary>
        /// Checks whether the player entity is able to use the chem master.
        /// </summary>
        /// <param name="playerEntity">The player entity.</param>
        /// <returns>Returns true if the entity can use the chem master, and false if it cannot.</returns>
        private bool PlayerCanUseChemMaster(IEntity playerEntity, bool needsPower = true)
        {
            //Need player entity to check if they are still able to use the chem master
            if (playerEntity == null)
                return false;
            //Check if player can interact in their current state
            if (!ActionBlockerSystem.CanInteract(playerEntity) || !ActionBlockerSystem.CanUse(playerEntity))
                return false;
            //Check if device is powered
            if (needsPower && !Powered)
                return false;

            return true;
        }

        /// <summary>
        /// Gets component data to be used to update the user interface client-side.
        /// </summary>
        /// <returns>Returns a <see cref="SharedChemMasterComponent.ChemMasterBoundUserInterfaceState"/></returns>
        private ChemMasterBoundUserInterfaceState GetUserInterfaceState()
        {
            var beaker = _beakerContainer.ContainedEntity;
            if (beaker == null)
            {
                return new ChemMasterBoundUserInterfaceState(Powered, false, ReagentUnit.New(0), ReagentUnit.New(0),
                    "", Owner.Name, null, BufferSolution.ReagentList.ToList(), BufferModeTransfer, BufferSolution.CurrentVolume, BufferSolution.MaxVolume);
            }

            var solution = beaker.GetComponent<SolutionComponent>();
            return new ChemMasterBoundUserInterfaceState(Powered, true, solution.CurrentVolume, solution.MaxVolume,
                beaker.Name, Owner.Name, solution.ReagentList.ToList(), BufferSolution.ReagentList.ToList(), BufferModeTransfer, BufferSolution.CurrentVolume, BufferSolution.MaxVolume);
        }

        private void UpdateUserInterface()
        {
            var state = GetUserInterfaceState();
            _userInterface.SetState(state);
        }

        /// <summary>
        /// If this component contains an entity with a <see cref="SolutionComponent"/>, eject it.
        /// Tries to eject into user's hands first, then ejects onto chem master if both hands are full.
        /// </summary>
        private void TryEject(IEntity user)
        {
            if (!HasBeaker)
                return;

            var beaker = _beakerContainer.ContainedEntity;
            _beakerContainer.Remove(_beakerContainer.ContainedEntity);
            UpdateUserInterface();

            if(!user.TryGetComponent<HandsComponent>(out var hands) || !beaker.TryGetComponent<ItemComponent>(out var item))
                return;
            if (hands.CanPutInHand(item))
                hands.PutInHand(item);
        }

        private void TransferReagent(string id, ReagentUnit amount, bool isBuffer)
        {
            if (!HasBeaker && BufferModeTransfer) return;
            var beaker = _beakerContainer.ContainedEntity;
            var beakerSolution = beaker.GetComponent<SolutionComponent>();
            if (isBuffer)
            {
                foreach (var reagent in BufferSolution.Solution.Contents)
                {
                    if (reagent.ReagentId == id)
                    {
                        ReagentUnit actualAmount;
                        if (amount == ReagentUnit.New(-1))
                        {
                            actualAmount = ReagentUnit.Min(reagent.Quantity, beakerSolution.EmptyVolume);
                        }
                        else
                        {
                            actualAmount = ReagentUnit.Min(reagent.Quantity, amount, beakerSolution.EmptyVolume);
                        }

                        BufferSolution.Solution.RemoveReagent(id, actualAmount);
                        if (BufferModeTransfer)
                        {
                            beakerSolution.Solution.AddReagent(id, actualAmount);
                        }
                        break;
                    }

                }
            }
            else
            {
                foreach (var reagent in beakerSolution.Solution.Contents)
                {
                    if (reagent.ReagentId == id)
                    {
                        ReagentUnit actualAmount;
                        if (amount == ReagentUnit.New(-1))
                        {
                            actualAmount = ReagentUnit.Min(reagent.Quantity, BufferSolution.EmptyVolume);
                        }
                        else
                        {
                            actualAmount = ReagentUnit.Min(reagent.Quantity, amount, BufferSolution.EmptyVolume);
                        }
                        beakerSolution.TryRemoveReagent(id, actualAmount);
                        BufferSolution.Solution.AddReagent(id, actualAmount);
                        break;
                    }
                }
            }

            UpdateUserInterface();
        }

        private void TryCreatePackage(IEntity user, UiAction action, int pillAmount, int bottleAmount)
        {
            var random = IoCManager.Resolve<IRobustRandom>();

            if (BufferSolution.CurrentVolume == 0)
                return;

            if (action == UiAction.CreateBottles)
            {
                var individualVolume = BufferSolution.CurrentVolume / ReagentUnit.New(bottleAmount);
                if (individualVolume < ReagentUnit.New(1))
                    return;

                var actualVolume = ReagentUnit.Min(individualVolume, ReagentUnit.New(30));
                for (int i = 0; i < bottleAmount; i++)
                {
                    var bottle = Owner.EntityManager.SpawnEntity("bottle", Owner.Transform.GridPosition);

                    var bufferSolution = BufferSolution.Solution.SplitSolution(actualVolume);

                    bottle.TryGetComponent<SolutionComponent>(out var bottleSolution);
                    bottleSolution?.Solution.AddSolution(bufferSolution);

                    //Try to give them the bottle
                    if (user.TryGetComponent<HandsComponent>(out var hands) &&
                        bottle.TryGetComponent<ItemComponent>(out var item))
                    {
                        if (hands.CanPutInHand(item))
                        {
                            hands.PutInHand(item);
                            continue;
                        }

                    }

                    //Put it on the floor
                    bottle.Transform.GridPosition = user.Transform.GridPosition;
                    //Give it an offset
                    var x_negative = random.Prob(0.5f) ? -1 : 1;
                    var y_negative = random.Prob(0.5f) ? -1 : 1;
                    bottle.Transform.LocalPosition += new Vector2(random.NextFloat() * 0.2f * x_negative, random.NextFloat() * 0.2f * y_negative);
                }

            }
            else //Pills
            {
                var individualVolume = BufferSolution.CurrentVolume / ReagentUnit.New(pillAmount);
                if (individualVolume < ReagentUnit.New(1))
                    return;

                var actualVolume = ReagentUnit.Min(individualVolume, ReagentUnit.New(50));
                for (int i = 0; i < pillAmount; i++)
                {
                    var pill = Owner.EntityManager.SpawnEntity("pill", Owner.Transform.GridPosition);

                    var bufferSolution = BufferSolution.Solution.SplitSolution(actualVolume);

                    pill.TryGetComponent<SolutionComponent>(out var pillSolution);
                    pillSolution?.Solution.AddSolution(bufferSolution);

                    //Try to give them the bottle
                    if (user.TryGetComponent<HandsComponent>(out var hands) &&
                        pill.TryGetComponent<ItemComponent>(out var item))
                    {
                        if (hands.CanPutInHand(item))
                        {
                            hands.PutInHand(item);
                            continue;
                        }

                    }

                    //Put it on the floor
                    pill.Transform.GridPosition = user.Transform.GridPosition;
                    //Give it an offset
                    var x_negative = random.Prob(0.5f) ? -1 : 1;
                    var y_negative = random.Prob(0.5f) ? -1 : 1;
                    pill.Transform.LocalPosition += new Vector2(random.NextFloat() * 0.2f * x_negative, random.NextFloat() * 0.2f * y_negative);
                }
            }

            UpdateUserInterface();
        }

        /// <summary>
        /// Called when you click the owner entity with an empty hand. Opens the UI client-side if possible.
        /// </summary>
        /// <param name="args">Data relevant to the event such as the actor which triggered it.</param>
        void IActivate.Activate(ActivateEventArgs args)
        {
            if (!args.User.TryGetComponent(out IActorComponent actor))
            {
                return;
            }

            if (!args.User.TryGetComponent(out IHandsComponent hands))
            {
                _notifyManager.PopupMessage(Owner.Transform.GridPosition, args.User,
                    _localizationManager.GetString("You have no hands."));
                return;
            }

            var activeHandEntity = hands.GetActiveHand?.Owner;
            if (activeHandEntity == null)
            {
                _userInterface.Open(actor.playerSession);
            }
        }

        /// <summary>
        /// Called when you click the owner entity with something in your active hand. If the entity in your hand
        /// contains a <see cref="SolutionComponent"/>, if you have hands, and if the chem master doesn't already
        /// hold a container, it will be added to the chem master.
        /// </summary>
        /// <param name="args">Data relevant to the event such as the actor which triggered it.</param>
        /// <returns></returns>
        async Task<bool> IInteractUsing.InteractUsing(InteractUsingEventArgs args)
        {
            if (!args.User.TryGetComponent(out IHandsComponent hands))
            {
                _notifyManager.PopupMessage(Owner.Transform.GridPosition, args.User,
                    _localizationManager.GetString("You have no hands."));
                return true;
            }

            var activeHandEntity = hands.GetActiveHand.Owner;
            if (activeHandEntity.TryGetComponent<SolutionComponent>(out var solution))
            {
                if (HasBeaker)
                {
                    _notifyManager.PopupMessage(Owner.Transform.GridPosition, args.User,
                        _localizationManager.GetString("This ChemMaster already has a container in it."));
                }
                else if ((solution.Capabilities & SolutionCaps.FitsInDispenser) == 0) //Close enough to a chem master...
                {
                    //If it can't fit in the chem master, don't put it in. For example, buckets and mop buckets can't fit.
                    _notifyManager.PopupMessage(Owner.Transform.GridPosition, args.User,
                        _localizationManager.GetString("That can't fit in the ChemMaster."));
                }
                else
                {
                    _beakerContainer.Insert(activeHandEntity);
                    UpdateUserInterface();
                }
            }
            else
            {
                _notifyManager.PopupMessage(Owner.Transform.GridPosition, args.User,
                    _localizationManager.GetString("You can't put this in the ChemMaster."));
            }

            return true;
        }

        void ISolutionChange.SolutionChanged(SolutionChangeEventArgs eventArgs) => UpdateUserInterface();

        private void ClickSound()
        {

            EntitySystem.Get<AudioSystem>().PlayFromEntity("/Audio/Machines/machine_switch.ogg", Owner, AudioParams.Default.WithVolume(-2f));
        }
    }
}

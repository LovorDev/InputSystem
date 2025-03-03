// UITK TreeView is not supported in earlier versions
// Therefore the UITK version of the InputActionAsset Editor is not available on earlier Editor versions either.
#if UNITY_EDITOR && UNITY_INPUT_SYSTEM_PROJECT_WIDE_ACTIONS
using CmdEvents = UnityEngine.InputSystem.Editor.InputActionsEditorConstants.CommandEvents;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace UnityEngine.InputSystem.Editor
{
    /// <summary>
    /// A view for displaying the actions of the selected action map in a tree with bindings
    /// as children.
    /// </summary>
    internal class ActionsTreeView : ViewBase<ActionsTreeView.ViewState>
    {
        private readonly TreeView m_ActionsTreeView;
        private readonly Button m_AddActionButton;
        private readonly ScrollView m_PropertiesScrollview;

        private bool m_RenameOnActionAdded;
        private readonly CollectionViewSelectionChangeFilter m_ActionsTreeViewSelectionChangeFilter;

        //save TreeView element id's of individual input actions and bindings to ensure saving of expanded state
        private Dictionary<Guid, int> m_GuidToTreeViewId;

        public ActionsTreeView(VisualElement root, StateContainer stateContainer)
            : base(root, stateContainer)
        {
            m_AddActionButton = root.Q<Button>("add-new-action-button");
            m_PropertiesScrollview = root.Q<ScrollView>("properties-scrollview");
            m_ActionsTreeView = root.Q<TreeView>("actions-tree-view");
            //assign unique viewDataKey to store treeView states like expanded/collapsed items - make it unique to avoid conflicts with other TreeViews
            m_ActionsTreeView.viewDataKey = "InputActionTreeView " + stateContainer.GetState().serializedObject.targetObject.GetInstanceID();
            m_GuidToTreeViewId = new Dictionary<Guid, int>();
            m_ActionsTreeView.selectionType = UIElements.SelectionType.Single;
            m_ActionsTreeView.makeItem = () => new InputActionsTreeViewItem();
            m_ActionsTreeView.bindItem = (e, i) =>
            {
                var item = m_ActionsTreeView.GetItemDataForIndex<ActionOrBindingData>(i);
                e.Q<Label>("name").text = item.name;
                var addBindingButton = e.Q<Button>("add-new-binding-button");
                addBindingButton.AddToClassList(EditorGUIUtility.isProSkin ? "add-binging-button-dark-theme" : "add-binging-button");
                var treeViewItem = (InputActionsTreeViewItem)e;
                treeViewItem.DeleteCallback = _ => DeleteItem(item);
                treeViewItem.DuplicateCallback = _ => DuplicateItem(item);
                treeViewItem.OnDeleteItem += treeViewItem.DeleteCallback;
                treeViewItem.OnDuplicateItem += treeViewItem.DuplicateCallback;
                if (item.isComposite)
                    ContextMenu.GetContextMenuForCompositeItem(treeViewItem, i);
                else if (item.isAction)
                    ContextMenu.GetContextMenuForActionItem(treeViewItem, item.controlLayout, i);
                else
                    ContextMenu.GetContextMenuForBindingItem(treeViewItem);

                if (item.isAction)
                {
                    addBindingButton.clicked += ContextMenu.GetContextMenuForActionAddItem(treeViewItem, item.controlLayout);
                    addBindingButton.clickable.activators.Add(new ManipulatorActivationFilter(){button = MouseButton.RightMouse});
                    addBindingButton.style.display = DisplayStyle.Flex;
                    treeViewItem.EditTextFinishedCallback = newName =>
                    {
                        m_RenameOnActionAdded = false;
                        ChangeActionName(item, newName);
                    };
                    treeViewItem.EditTextFinished += treeViewItem.EditTextFinishedCallback;
                }
                else
                {
                    addBindingButton.style.display = DisplayStyle.None;
                    if (!item.isComposite)
                        treeViewItem.UnregisterInputField();
                    else
                    {
                        treeViewItem.EditTextFinishedCallback = newName =>
                        {
                            m_RenameOnActionAdded = false;
                            ChangeCompositeName(item, newName);
                        };
                        treeViewItem.EditTextFinished += treeViewItem.EditTextFinishedCallback;
                    }
                }

                if (!string.IsNullOrEmpty(item.controlLayout))
                    e.Q<VisualElement>("icon").style.backgroundImage =
                        new StyleBackground(
                            EditorInputControlLayoutCache.GetIconForLayout(item.controlLayout));
                else
                    e.Q<VisualElement>("icon").style.backgroundImage =
                        new StyleBackground(
                            EditorInputControlLayoutCache.GetIconForLayout("Control"));
            };

            m_ActionsTreeView.itemsChosen += objects =>
            {
                var data = (ActionOrBindingData)objects.First();
                if (!data.isAction && !data.isComposite)
                    return;
                var item = m_ActionsTreeView.GetRootElementForIndex(m_ActionsTreeView.selectedIndex).Q<InputActionsTreeViewItem>();
                item.FocusOnRenameTextField();
            };

            m_ActionsTreeView.unbindItem = (element, i) =>
            {
                var item = m_ActionsTreeView.GetItemDataForIndex<ActionOrBindingData>(i);
                var treeViewItem = (InputActionsTreeViewItem)element;
                //reset the editing variable before reassigning visual elements
                if (item.isAction || item.isComposite)
                    treeViewItem.Reset();

                treeViewItem.OnDeleteItem -= treeViewItem.DeleteCallback;
                treeViewItem.OnDuplicateItem -= treeViewItem.DuplicateCallback;
                treeViewItem.EditTextFinished -= treeViewItem.EditTextFinishedCallback;
            };

            ContextMenu.GetContextMenuForActionListView(this, m_ActionsTreeView, m_ActionsTreeView.parent);

            m_ActionsTreeViewSelectionChangeFilter = new CollectionViewSelectionChangeFilter(m_ActionsTreeView);
            m_ActionsTreeViewSelectionChangeFilter.selectedIndicesChanged += (_) =>
            {
                if (m_ActionsTreeView.selectedIndex >= 0)
                {
                    var item = m_ActionsTreeView.GetItemDataForIndex<ActionOrBindingData>(m_ActionsTreeView.selectedIndex);
                    Dispatch(item.isAction ? Commands.SelectAction(item.name) : Commands.SelectBinding(item.bindingIndex));
                }
                else
                {
                    Dispatch(Commands.SelectAction(null));
                    Dispatch(Commands.SelectBinding(-1));
                }
            };

            m_ActionsTreeView.RegisterCallback<ExecuteCommandEvent>(OnExecuteCommand);
            m_ActionsTreeView.RegisterCallback<ValidateCommandEvent>(OnValidateCommand);

            CreateSelector(Selectors.GetActionsForSelectedActionMap, Selectors.GetActionMapCount,
                (_, count, state) =>
                {
                    var treeData = Selectors.GetActionsAsTreeViewData(state, m_GuidToTreeViewId);
                    return new ViewState
                    {
                        treeViewData = treeData,
                        actionMapCount = count ?? 0,
                        newElementID = GetSelectedElementId(state, treeData)
                    };
                });

            m_AddActionButton.clicked += AddAction;
        }

        private int GetSelectedElementId(InputActionsEditorState state, List<TreeViewItemData<ActionOrBindingData>> treeData)
        {
            var id = -1;
            if (state.selectionType == SelectionType.Action)
            {
                if (treeData.Count > state.selectedActionIndex && state.selectedActionIndex >= 0)
                    id = treeData[state.selectedActionIndex].id;
            }
            else if (state.selectionType == SelectionType.Binding)
                id = GetComponentOrBindingID(treeData, state.selectedBindingIndex);
            return id;
        }

        private int GetComponentOrBindingID(List<TreeViewItemData<ActionOrBindingData>> treeItemList, int selectedBindingIndex)
        {
            foreach (var actionItem in treeItemList)
            {
                // Look for the element ID by checking if the selected binding index matches the binding index of
                // the ActionOrBindingData of the item. Deals with composite bindings as well.
                foreach (var bindingOrComponentItem in actionItem.children)
                {
                    if (bindingOrComponentItem.data.bindingIndex == selectedBindingIndex)
                        return bindingOrComponentItem.id;
                    if (bindingOrComponentItem.hasChildren)
                    {
                        foreach (var bindingItem in bindingOrComponentItem.children)
                        {
                            if (bindingOrComponentItem.data.bindingIndex == selectedBindingIndex)
                                return bindingItem.id;
                        }
                    }
                }
            }
            return -1;
        }

        public override void DestroyView()
        {
            m_AddActionButton.clicked -= AddAction;
        }

        public override void RedrawUI(ViewState viewState)
        {
            m_ActionsTreeView.Clear();
            m_ActionsTreeView.SetRootItems(viewState.treeViewData);
            m_ActionsTreeView.Rebuild();
            if (viewState.newElementID != -1)
            {
                m_ActionsTreeView.SetSelectionById(viewState.newElementID);
                m_ActionsTreeView.ScrollToItemById(viewState.newElementID);
            }
            RenameNewAction(viewState.newElementID);;
            m_AddActionButton.SetEnabled(viewState.actionMapCount > 0);

            // Don't want to show action properties if there's no actions.
            m_PropertiesScrollview.visible = m_ActionsTreeView.GetTreeCount() > 0;
        }

        private void RenameNewAction(int id)
        {
            if (!m_RenameOnActionAdded || id == -1)
                return;
            m_ActionsTreeView.ScrollToItemById(id);
            var treeViewItem = m_ActionsTreeView.GetRootElementForId(id)?.Q<InputActionsTreeViewItem>();
            treeViewItem?.FocusOnRenameTextField();
        }

        internal void AddAction()
        {
            Dispatch(Commands.AddAction());
            m_RenameOnActionAdded = true;
        }

        internal void AddBinding(string actionName)
        {
            Dispatch(Commands.SelectAction(actionName));
            Dispatch(Commands.AddBinding());
        }

        internal void AddComposite(string actionName, string compositeType)
        {
            Dispatch(Commands.SelectAction(actionName));
            Dispatch(Commands.AddComposite(compositeType));
        }

        private void DeleteItem(ActionOrBindingData data)
        {
            if (data.isAction)
            {
                string actionToSelect = GetPreviousActionNameFromViewTree(data);
                Dispatch(Commands.DeleteAction(data.actionMapIndex, data.name));
                Dispatch(Commands.SelectAction(actionToSelect));
            }
            else
            {
                int bindingIndexToSelect = GetPreviousBindingIndexFromViewTree(data, out string parentActionName);
                Dispatch(Commands.DeleteBinding(data.actionMapIndex, data.bindingIndex));

                if (bindingIndexToSelect >= 0)
                    Dispatch(Commands.SelectBinding(bindingIndexToSelect));
                else
                    Dispatch(Commands.SelectAction(parentActionName));
            }

            // Deleting an item sometimes causes the UI Panel to lose focus; make sure we keep it
            m_ActionsTreeView.Focus();
        }

        private void DuplicateItem(ActionOrBindingData data)
        {
            Dispatch(data.isAction ? Commands.DuplicateAction() : Commands.DuplicateBinding());
        }

        internal void CopyItems()
        {
            Dispatch(Commands.CopyActionBindingSelection());
        }

        internal void CutItems()
        {
            Dispatch(Commands.CutActionsOrBindings());
        }

        internal void PasteItems()
        {
            Dispatch(Commands.PasteActionsOrBindings());
        }

        private void ChangeActionName(ActionOrBindingData data, string newName)
        {
            m_RenameOnActionAdded = false;
            Dispatch(Commands.ChangeActionName(data.actionMapIndex, data.name, newName));
        }

        private void ChangeCompositeName(ActionOrBindingData data, string newName)
        {
            m_RenameOnActionAdded = false;
            Dispatch(Commands.ChangeCompositeName(data.actionMapIndex, data.bindingIndex, newName));
        }

        private void OnExecuteCommand(ExecuteCommandEvent evt)
        {
            if (m_ActionsTreeView.selectedItem == null)
                return;

            var data = (ActionOrBindingData)m_ActionsTreeView.selectedItem;
            switch (evt.commandName)
            {
                case CmdEvents.Rename:
                    if (data.isAction || data.isComposite)
                        m_ActionsTreeView.GetRootElementForIndex(m_ActionsTreeView.selectedIndex)?.Q<InputActionsTreeViewItem>()?.FocusOnRenameTextField();
                    else
                        return;
                    break;
                case CmdEvents.Delete:
                case CmdEvents.SoftDelete:
                    m_ActionsTreeView.GetRootElementForIndex(m_ActionsTreeView.selectedIndex)?.Q<InputActionsTreeViewItem>()?.DeleteItem();
                    break;
                case CmdEvents.Duplicate:
                    m_ActionsTreeView.GetRootElementForIndex(m_ActionsTreeView.selectedIndex)?.Q<InputActionsTreeViewItem>()?.DuplicateItem();
                    break;
                case CmdEvents.Copy:
                    CopyItems();
                    break;
                case CmdEvents.Cut:
                    CutItems();
                    break;
                case CmdEvents.Paste:
                    var hasPastableData = CopyPasteHelper.HasPastableClipboardData(data.isAction ? typeof(InputAction) : typeof(InputBinding));
                    if (hasPastableData)
                        PasteItems();
                    break;
                default:
                    return; // Skip StopPropagation if we didn't execute anything
            }
            evt.StopPropagation();
        }

        private void OnValidateCommand(ValidateCommandEvent evt)
        {
            // Mark commands as supported for Execute by stopping propagation of the event
            switch (evt.commandName)
            {
                case CmdEvents.Rename:
                case CmdEvents.Delete:
                case CmdEvents.SoftDelete:
                case CmdEvents.Duplicate:
                case CmdEvents.Copy:
                case CmdEvents.Cut:
                case CmdEvents.Paste:
                    evt.StopPropagation();
                    break;
            }
        }

        private string GetPreviousActionNameFromViewTree(in ActionOrBindingData data)
        {
            Debug.Assert(data.isAction);

            // If TreeView currently (before delete) has more than one Action, select the one immediately
            // above or immediately below depending if data is first in the list
            var treeView = ViewStateSelector.GetViewState(stateContainer.GetState()).treeViewData;
            if (treeView.Count > 1)
            {
                string actionName = data.name;
                int index = treeView.FindIndex(item => item.data.name == actionName);
                if (index > 0)
                    index--;
                else
                    index++; // Also handles case if actionName wasn't found; FindIndex() returns -1 that's incremented to 0

                return treeView[index].data.name;
            }

            return string.Empty;
        }

        private int GetPreviousBindingIndexFromViewTree(in ActionOrBindingData data, out string parentActionName)
        {
            Debug.Assert(!data.isAction);

            int retVal = -1;
            parentActionName = string.Empty;

            // The bindindIndex is global and doesn't correspond to the binding's "child index" within the TreeView.
            // To find the "previous" Binding to select, after deleting the current one, we must:
            // 1. Traverse the ViewTree to find the parent of the binding and its index under that parent
            // 2. Identify the Binding to select after deletion and retrieve its bindingIndex
            // 3. Return the bindingIndex and the parent Action name (select the Action if bindingIndex is invalid)

            var treeView = ViewStateSelector.GetViewState(stateContainer.GetState()).treeViewData;
            foreach (var action in treeView)
            {
                if (!action.hasChildren)
                    continue;

                if (FindBindingOrComponentTreeViewParent(action, data.bindingIndex, out var parentNode, out int childIndex))
                {
                    parentActionName = action.data.name;
                    if (parentNode.children.Count() > 1)
                    {
                        int prevIndex = Math.Max(childIndex - 1, 0);
                        var node = parentNode.children.ElementAt(prevIndex);
                        retVal = node.data.bindingIndex;
                        break;
                    }
                }
            }

            return retVal;
        }

        private static bool FindBindingOrComponentTreeViewParent(TreeViewItemData<ActionOrBindingData> root, int bindingIndex, out TreeViewItemData<ActionOrBindingData> parent, out int childIndex)
        {
            Debug.Assert(root.hasChildren);

            int index = 0;
            foreach (var item in root.children)
            {
                if (item.data.bindingIndex == bindingIndex)
                {
                    parent = root;
                    childIndex = index;
                    return true;
                }

                if (item.hasChildren && FindBindingOrComponentTreeViewParent(item, bindingIndex, out parent, out childIndex))
                    return true;

                index++;
            }

            parent = default;
            childIndex = -1;
            return false;
        }

        internal class ViewState
        {
            public List<TreeViewItemData<ActionOrBindingData>> treeViewData;
            public int actionMapCount;
            public int newElementID;
        }
    }

    internal struct ActionOrBindingData
    {
        public ActionOrBindingData(bool isAction, string name, int actionMapIndex, bool isComposite = false, string controlLayout = "", int bindingIndex = -1)
        {
            this.name = name;
            this.isComposite = isComposite;
            this.actionMapIndex = actionMapIndex;
            this.controlLayout = controlLayout;
            this.bindingIndex = bindingIndex;
            this.isAction = isAction;
        }

        public string name { get; }
        public bool isAction { get; }
        public int actionMapIndex { get; }
        public bool isComposite { get; }
        public string controlLayout { get; }
        public int bindingIndex { get; }
    }

    internal static partial class Selectors
    {
        public static List<TreeViewItemData<ActionOrBindingData>> GetActionsAsTreeViewData(InputActionsEditorState state, Dictionary<Guid, int> idDictionary)
        {
            var actionMapIndex = state.selectedActionMapIndex;
            var controlSchemes = state.serializedObject.FindProperty(nameof(InputActionAsset.m_ControlSchemes));
            var actionMap = GetSelectedActionMap(state);

            if (actionMap == null)
                return new List<TreeViewItemData<ActionOrBindingData>>();

            var actions = actionMap.Value.wrappedProperty
                .FindPropertyRelative(nameof(InputActionMap.m_Actions))
                .Select(sp => new SerializedInputAction(sp));

            var bindings = actionMap.Value.wrappedProperty
                .FindPropertyRelative(nameof(InputActionMap.m_Bindings))
                .Select(sp => new SerializedInputBinding(sp))
                .ToList();

            var actionItems = new List<TreeViewItemData<ActionOrBindingData>>();
            foreach (var action in actions)
            {
                var actionBindings = bindings.Where(spb => spb.action == action.name).ToList();
                var bindingItems = new List<TreeViewItemData<ActionOrBindingData>>();
                var actionId = new Guid(action.id);

                for (var i = 0; i < actionBindings.Count; i++)
                {
                    var serializedInputBinding = actionBindings[i];
                    var inputBindingId = new Guid(serializedInputBinding.id);

                    if (serializedInputBinding.isComposite)
                    {
                        var compositeItems = new List<TreeViewItemData<ActionOrBindingData>>();
                        var nextBinding = actionBindings[++i];
                        while (nextBinding.isPartOfComposite)
                        {
                            var isVisible = ShouldBindingBeVisible(nextBinding, state.selectedControlScheme);
                            if (isVisible)
                            {
                                var name = GetHumanReadableCompositeName(nextBinding, state.selectedControlScheme, controlSchemes);
                                var compositeBindingId = new Guid(nextBinding.id);
                                compositeItems.Add(new TreeViewItemData<ActionOrBindingData>(GetIdForGuid(new Guid(nextBinding.id), idDictionary),
                                    new ActionOrBindingData(false, name, actionMapIndex, false,
                                        GetControlLayout(nextBinding.path), nextBinding.indexOfBinding)));
                            }

                            if (++i >= actionBindings.Count)
                                break;

                            nextBinding = actionBindings[i];
                        }
                        i--;
                        bindingItems.Add(new TreeViewItemData<ActionOrBindingData>(GetIdForGuid(inputBindingId, idDictionary),
                            new ActionOrBindingData(false, serializedInputBinding.name, actionMapIndex, true, action.expectedControlType, serializedInputBinding.indexOfBinding),
                            compositeItems.Count > 0 ? compositeItems : null));
                    }
                    else
                    {
                        var isVisible = ShouldBindingBeVisible(serializedInputBinding, state.selectedControlScheme);
                        if (isVisible)
                            bindingItems.Add(new TreeViewItemData<ActionOrBindingData>(GetIdForGuid(inputBindingId, idDictionary),
                                new ActionOrBindingData(false, GetHumanReadableBindingName(serializedInputBinding, state.selectedControlScheme, controlSchemes), actionMapIndex,
                                    false, GetControlLayout(serializedInputBinding.path), serializedInputBinding.indexOfBinding)));
                    }
                }
                actionItems.Add(new TreeViewItemData<ActionOrBindingData>(GetIdForGuid(actionId, idDictionary),
                    new ActionOrBindingData(true, action.name, actionMapIndex, false, action.expectedControlType), bindingItems.Count > 0 ? bindingItems : null));
            }
            return actionItems;
        }

        private static int GetIdForGuid(Guid guid, Dictionary<Guid, int> idDictionary)
        {
            if (!idDictionary.TryGetValue(guid, out var id))
            {
                id = idDictionary.Values.Count > 0 ? idDictionary.Values.Max() + 1 : 0;
                idDictionary.Add(guid, id);
            }
            return id;
        }

        private static string GetHumanReadableBindingName(SerializedInputBinding serializedInputBinding, InputControlScheme? currentControlScheme, SerializedProperty allControlSchemes)
        {
            var name = InputControlPath.ToHumanReadableString(serializedInputBinding.path);
            if (String.IsNullOrEmpty(name))
                name = "<No Binding>";
            if (IsBindingAssignedToNoControlSchemes(serializedInputBinding, allControlSchemes, currentControlScheme))
                name += " {GLOBAL}";
            return name;
        }

        private static bool IsBindingAssignedToNoControlSchemes(SerializedInputBinding serializedInputBinding, SerializedProperty allControlSchemes, InputControlScheme? currentControlScheme)
        {
            if (allControlSchemes.arraySize <= 0 || !currentControlScheme.HasValue || string.IsNullOrEmpty(currentControlScheme.Value.name))
                return false;
            if (serializedInputBinding.controlSchemes.Length <= 0)
                return true;
            return false;
        }

        private static bool ShouldBindingBeVisible(SerializedInputBinding serializedInputBinding, InputControlScheme? currentControlScheme)
        {
            if (currentControlScheme.HasValue && !string.IsNullOrEmpty(currentControlScheme.Value.name))
            {
                //if binding is global (not assigned to any control scheme) show always
                if (serializedInputBinding.controlSchemes.Length <= 0)
                    return true;
                return serializedInputBinding.controlSchemes.Contains(currentControlScheme.Value.name);
            }
            //if no control scheme selected then show all bindings
            return true;
        }

        internal static string GetHumanReadableCompositeName(SerializedInputBinding binding, InputControlScheme? currentControlScheme, SerializedProperty allControlSchemes)
        {
            return $"{ObjectNames.NicifyVariableName(binding.name)}: " +
                $"{GetHumanReadableBindingName(binding, currentControlScheme, allControlSchemes)}";
        }

        private static string GetControlLayout(string path)
        {
            var controlLayout = string.Empty;
            try
            {
                controlLayout = InputControlPath.TryGetControlLayout(path);
            }
            catch (Exception)
            {
            }

            return controlLayout;
        }
    }
}

#endif

// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DvmConsole.Avalonia.ViewModels;

namespace DvmConsole.Avalonia.Views
{
    /// <summary>
    /// Owner-bound Groups editor shell. The published view model owns all
    /// managed editing semantics and request payloads; this window owns only
    /// bindings and user gestures. Persistence and runtime patch routing stay
    /// with the MainWindow and later runtime-routing composition gates.
    /// </summary>
    internal partial class PatchGroupsWindow : Window
    {
        private readonly PatchGroupsViewModel viewModel;

        public PatchGroupsWindow(PatchGroupsViewModel viewModel)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = viewModel;
            Closed += OnWindowClosed;
        }

        private void Group_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm
                && sender is ListBox list
                && list.SelectedItem is PatchGroupsViewModel.GroupState group)
            {
                vm.SelectedGroup = group;
            }
        }

        private void Edit_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm && vm.SelectedGroup is { } group)
            {
                vm.EnterEdit(group.Name);
            }
        }

        private void Done_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm && vm.SelectedGroup is { } group)
            {
                vm.ExitEdit(group.Name);
            }
        }

        private void AddMember_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm
                && vm.SelectedGroup is { } group
                && vm.SelectedMember is { } member)
            {
                vm.AddMember(group.Name, member.SystemName, member.Tgid);
            }
        }

        private void RemoveMember_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not PatchGroupsViewModel vm
                || vm.SelectedGroup is not { } group
                || vm.SelectedMemberRow is not { } member)
            {
                return;
            }

            int index = group.Members.ToList().IndexOf(member);
            if (index >= 0)
            {
                vm.RemoveMember(group.Name, index);
                vm.SelectedMemberRow = null;
            }
        }

        private void MoveMember_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not PatchGroupsViewModel vm
                || vm.SelectedGroup is not { } group
                || vm.SelectedMemberRow is not { } member
                || sender is not Button button)
            {
                return;
            }

            int fromIndex = group.Members.ToList().IndexOf(member);
            if (fromIndex < 0)
            {
                return;
            }

            int toIndex = string.Equals(button.Tag?.ToString(), "up", StringComparison.OrdinalIgnoreCase)
                ? fromIndex - 1
                : fromIndex + 1;
            if (vm.MoveMember(group.Name, fromIndex, toIndex))
            {
                vm.SelectedMemberRow = group.Members[toIndex];
            }
        }

        private void OneWay_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm
                && vm.SelectedGroup is { } group
                && sender is CheckBox checkBox
                && checkBox.IsChecked is bool value)
            {
                vm.SetOneWay(group.Name, value);
            }
        }

        private void Enabled_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm
                && vm.SelectedGroup is { } group
                && sender is CheckBox checkBox
                && checkBox.IsChecked is bool value)
            {
                vm.SetEnabled(group.Name, value);
            }
        }

        private void Ptt_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm && vm.SelectedGroup is { } group)
            {
                vm.RequestPtt(group.Name);
            }
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (DataContext is PatchGroupsViewModel vm)
            {
                vm.Commit();
            }
        }

        private void Close_Click(object? sender, RoutedEventArgs e)
            => Close();

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            Closed -= OnWindowClosed;
            viewModel.Close();
        }
    }
}

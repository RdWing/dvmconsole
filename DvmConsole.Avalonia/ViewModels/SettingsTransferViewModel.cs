// SPDX-License-Identifier: AGPL-3.0-only
#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DvmConsole.Avalonia.Services;
using dvmconsole;

namespace DvmConsole.Avalonia.ViewModels
{
    /// <summary>
    /// Headless category-selection surface for settings transfer and reset.
    /// It owns selection state and confirmation sequencing; file dialogs and
    /// runtime composition stay in the Avalonia shell.
    /// </summary>
    public sealed class SettingsTransferViewModel
    {
        private readonly SettingsTransferService service;

        public SettingsTransferViewModel(SettingsTransferService service)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            Categories = service.Categories
                .Select(category => new SettingsTransferCategoryItem(category))
                .ToArray();
        }

        public IReadOnlyList<SettingsTransferCategoryItem> Categories { get; }

        public SettingsTransferCategoryItem? FindCategory(string id)
            => Categories.FirstOrDefault(category =>
                string.Equals(category.Id, id, StringComparison.OrdinalIgnoreCase));

        public void SelectAll()
        {
            foreach (SettingsTransferCategoryItem category in Categories)
            {
                category.IsSelected = true;
            }
        }

        public void SelectNone()
        {
            foreach (SettingsTransferCategoryItem category in Categories)
            {
                category.IsSelected = false;
            }
        }

        public Task<bool> ExportAsync(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(service.Export(filePath, SelectedIds()));
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        public async Task<bool> ImportAsync(
            string filePath,
            Func<Task<bool>> confirmAsync,
            Func<Task> reloadRuntimeAsync,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(confirmAsync);
            ArgumentNullException.ThrowIfNull(reloadRuntimeAsync);
            cancellationToken.ThrowIfCancellationRequested();

            if (!await confirmAsync())
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                service.Import(filePath, SelectedIds());
            }
            catch
            {
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            await reloadRuntimeAsync();
            return true;
        }

        public async Task<bool> ResetAsync(
            Func<Task<bool>> confirmAsync,
            Func<Task>? reloadRuntimeAsync = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(confirmAsync);
            cancellationToken.ThrowIfCancellationRequested();

            if (!await confirmAsync())
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            service.Reset();
            if (reloadRuntimeAsync is not null)
            {
                await reloadRuntimeAsync();
            }

            return true;
        }

        private IEnumerable<string> SelectedIds()
            => Categories.Where(category => category.IsSelected).Select(category => category.Id);
    }

    public sealed class SettingsTransferCategoryItem : INotifyPropertyChanged
    {
        private bool isSelected = true;

        internal SettingsTransferCategoryItem(SettingsTransferCategoryDefinition definition)
        {
            Id = definition.Id;
            DisplayName = definition.DisplayName;
            Description = definition.Description;
            PropertyNames = definition.PropertyNames;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string Description { get; }

        public IReadOnlyList<string> PropertyNames { get; }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                {
                    return;
                }

                isSelected = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

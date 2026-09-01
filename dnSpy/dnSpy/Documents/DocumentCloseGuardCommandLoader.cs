// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Extension;

namespace dnSpy.Documents {
	/// <summary>Runs the centralized close guards when the application exits.</summary>
	[ExportAutoLoaded]
	sealed class DocumentCloseGuardCommandLoader : IAutoLoaded {
		readonly IDsDocumentService documentService;
		readonly IDsDocumentCloseGuardService closeGuardService;

		[ImportingConstructor]
		DocumentCloseGuardCommandLoader(IAppWindow appWindow, IDsDocumentService documentService,
			IDsDocumentCloseGuardService closeGuardService) {
			this.documentService = documentService;
			this.closeGuardService = closeGuardService;
			appWindow.MainWindowClosing += AppWindow_MainWindowClosing;
		}

		void AppWindow_MainWindowClosing(object? sender, CancelEventArgs e) {
			IDsDocument[] documents = documentService.GetDocuments();
			if (!closeGuardService.TryExecute(documents, DsDocumentCloseReason.AppExit,
				static () => true))
				e.Cancel = true;
		}
	}
}

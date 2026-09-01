// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;

namespace dnSpy.Documents {
	interface IDsDocumentCloseGuardServiceInternal {
		bool TryExecuteClear(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason,
			Func<bool> authorizedAction);
	}

	/// <summary>
	/// Central coordinator for document close guards. All guard evaluation and authorized
	/// mutations happen synchronously on the main window dispatcher.
	/// </summary>
	[Export(typeof(IDsDocumentCloseGuardService))]
	public sealed class DsDocumentCloseGuardService : IDsDocumentCloseGuardService, IDsDocumentCloseGuardServiceInternal {
		readonly IAppWindow appWindow;
		readonly Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata>[] guards;

		// These fields are only accessed by the main-window dispatcher. Dispatcher.Invoke()
		// serializes worker-thread callers before they reach them.
		bool evaluatingGuards;
		AuthorizationFrame? authorization;

		sealed class AuthorizationFrame {
			public AuthorizationFrame(IDsDocument[] documents, DsDocumentCloseReason reason) {
				Documents = documents;
				Reason = reason;
				DocumentSet = new HashSet<IDsDocument>(documents, new HashSetComparer());
			}

			public IDsDocument[] Documents { get; }
			public DsDocumentCloseReason Reason { get; }
			public HashSet<IDsDocument> DocumentSet { get; }
			public bool Consumed { get; set; }

			public bool Matches(IReadOnlyList<IDsDocument> documents) {
				if (Consumed || documents.Count != Documents.Length)
					return false;
				var candidateSet = new HashSet<IDsDocument>(new HashSetComparer());
				for (int i = 0; i < documents.Count; i++) {
					IDsDocument? document = documents[i];
					if (document is null || !candidateSet.Add(document))
						return false;
				}
				return candidateSet.SetEquals(DocumentSet);
			}
		}

		sealed class HashSetComparer : IEqualityComparer<IDsDocument> {
			public bool Equals(IDsDocument? x, IDsDocument? y) => ReferenceEquals(x, y);
			public int GetHashCode(IDsDocument obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
		}

		[ImportingConstructor]
		public DsDocumentCloseGuardService(IAppWindow appWindow,
			[ImportMany] IEnumerable<Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata>> guards) {
			this.appWindow = appWindow ?? throw new ArgumentNullException(nameof(appWindow));
			if (guards is null)
				throw new ArgumentNullException(nameof(guards));

			// Enumerate the MEF sequence exactly once. Metadata is validated before any guard is
			// evaluated so a malformed composition cannot result in partially ordered execution.
			var materialized = guards.ToArray();
			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata> guard in materialized) {
				if (guard.Metadata is null || string.IsNullOrEmpty(guard.Metadata.Name))
					throw new InvalidOperationException("A document close guard must have a non-empty name.");
				if (double.IsNaN(guard.Metadata.Order))
					throw new InvalidOperationException(
						$"Document close guard '{guard.Metadata.Name}' has an invalid order.");
				if (!names.Add(guard.Metadata.Name))
					throw new InvalidOperationException(
						$"Duplicate document close guard name '{guard.Metadata.Name}'.");
			}

			this.guards = materialized
				.OrderBy(a => a.Metadata.Order)
				.ThenBy(a => a.Metadata.Name, StringComparer.Ordinal)
				.ToArray();
		}

		/// <inheritdoc/>
		public bool TryExecute(IReadOnlyList<IDsDocument> documents,
			DsDocumentCloseReason reason, Func<bool> authorizedAction) {
			if (documents is null)
				throw new ArgumentNullException(nameof(documents));
			if (authorizedAction is null)
				throw new ArgumentNullException(nameof(authorizedAction));
			if (!Enum.IsDefined(typeof(DsDocumentCloseReason), reason))
				return false;

			// Copy caller-owned collections before crossing a thread boundary. This also ensures
			// that guards see one stable document-reference snapshot for the entire operation.
			IDsDocument[] snapshot;
			try {
				if (!TryCopyDistinct(documents, out snapshot))
					return false;
			}
			catch (Exception ex) {
				ReportFailure("Unable to snapshot documents for a close operation.", ex);
				return false;
			}

			try {
				Dispatcher dispatcher = appWindow.MainWindow.Dispatcher;
				if (dispatcher.CheckAccess())
					return TryExecuteCore(snapshot, reason, authorizedAction);
				return (bool)dispatcher.Invoke(DispatcherPriority.Send,
					new Func<bool>(() => TryExecuteCore(snapshot, reason, authorizedAction)));
			}
			catch (Exception ex) {
				ReportFailure("Unable to execute a document close operation.", ex);
				return false;
			}
		}

		/// <summary>
		/// Executes the document service's direct Clear operation. When a list operation is already
		/// authorized, the one matching nested Clear consumes that authorization without prompting
		/// again. This method is deliberately internal; callers cannot use it to bypass the service.
		/// </summary>
		internal bool TryExecuteClear(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason,
			Func<bool> authorizedAction) {
			if (documents is null)
				throw new ArgumentNullException(nameof(documents));
			if (authorizedAction is null)
				throw new ArgumentNullException(nameof(authorizedAction));
			if (!Enum.IsDefined(typeof(DsDocumentCloseReason), reason))
				return false;

			IDsDocument[] snapshot;
			try {
				if (!TryCopyDistinct(documents, out snapshot))
					return false;
			}
			catch (Exception ex) {
				ReportFailure("Unable to snapshot documents for a clear operation.", ex);
				return false;
			}
			try {
				Dispatcher dispatcher = appWindow.MainWindow.Dispatcher;
				if (dispatcher.CheckAccess())
					return TryExecuteClearCore(snapshot, reason, authorizedAction);
				return (bool)dispatcher.Invoke(DispatcherPriority.Send,
					new Func<bool>(() => TryExecuteClearCore(snapshot, reason, authorizedAction)));
			}
			catch (Exception ex) {
				ReportFailure("Unable to execute a document clear operation.", ex);
				return false;
			}
		}

		bool IDsDocumentCloseGuardServiceInternal.TryExecuteClear(IReadOnlyList<IDsDocument> documents,
			DsDocumentCloseReason reason, Func<bool> authorizedAction) =>
			TryExecuteClear(documents, reason, authorizedAction);

		bool TryExecuteClearCore(IDsDocument[] documents, DsDocumentCloseReason reason,
			Func<bool> authorizedAction) {
			if (authorization is not null) {
				// A nested Clear is the sole operation allowed to consume an authorization frame.
				// A different document set/reason, a second consumption, or a normal nested operation
				// fails closed without evaluating guards or mutating documents.
				if (authorization.Reason != reason || !authorization.Matches(documents))
					return false;
				authorization.Consumed = true;
				return InvokeAuthorizedAction(authorizedAction);
			}
			if (evaluatingGuards)
				return false;
			return TryExecuteCore(documents, reason, authorizedAction);
		}

		static bool TryCopyDistinct(IReadOnlyList<IDsDocument> documents, out IDsDocument[] snapshot) {
			var result = new List<IDsDocument>(documents.Count);
			var seen = new HashSet<IDsDocument>(new HashSetComparer());
			for (int i = 0; i < documents.Count; i++) {
				IDsDocument? document = documents[i];
				if (document is null) {
					snapshot = Array.Empty<IDsDocument>();
					return false;
				}
				if (!seen.Add(document))
					continue;
				result.Add(document);
			}
			snapshot = result.ToArray();
			return true;
		}

		bool TryExecuteCore(IDsDocument[] documents, DsDocumentCloseReason reason,
			Func<bool> authorizedAction) {
			// Normal nested operations are never authorized by an outer frame.
			if (authorization is not null || evaluatingGuards)
				return false;

			evaluatingGuards = true;
			try {
				foreach (Lazy<IDsDocumentCloseGuard, IDsDocumentCloseGuardMetadata> guard in guards) {
					bool canClose;
					try {
						canClose = guard.Value.CanClose(documents, reason);
					}
					catch (Exception ex) {
						ReportFailure($"Document close guard '{guard.Metadata.Name}' failed; the operation was canceled.", ex);
						return false;
					}
					if (!canClose)
						return false;
				}
			}
			finally {
				evaluatingGuards = false;
			}

			// The frame is visible only while this authorized callback runs. Clear() may consume it
			// once, but any other removal path remains rejected.
			var frame = new AuthorizationFrame(documents, reason);
			authorization = frame;
			try {
				return InvokeAuthorizedAction(authorizedAction);
			}
			finally {
				if (ReferenceEquals(authorization, frame))
					authorization = null;
			}
		}

		static bool InvokeAuthorizedAction(Func<bool> authorizedAction) {
			try {
				return authorizedAction();
			}
			catch (Exception ex) {
				ReportFailure("The authorized document operation failed; the operation was canceled.", ex);
				return false;
			}
		}

		static void ReportFailure(string message, Exception exception) {
			Debug.WriteLine($"{message} {exception}");
			try {
				MsgBox.Instance.Show(exception, message);
			}
			catch {
				// MessageBox initialization can legitimately lag service construction (and tests may
				// intentionally omit it). Failing closed is still guaranteed in that case.
			}
		}
	}
}

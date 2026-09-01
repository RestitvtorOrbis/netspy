// Copyright (C) 2026 netSpy Single-File contributors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace dnSpy.Contracts.Documents {
	/// <summary>Reason why a set of documents is about to be closed.</summary>
	public enum DsDocumentCloseReason {
		/// <summary>A document was explicitly removed.</summary>
		Remove,
		/// <summary>The current document list is being reloaded.</summary>
		ReloadList,
		/// <summary>A different document list is being loaded.</summary>
		LoadList,
		/// <summary>The application is exiting.</summary>
		AppExit,
	}

	/// <summary>Can prevent closing a set of top-level documents.</summary>
	public interface IDsDocumentCloseGuard {
		/// <summary>
		/// Returns <see langword="true"/> when the documents may be closed. The call is made on the
		/// main window dispatcher.
		/// </summary>
		bool CanClose(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason);
	}

	/// <summary>Metadata associated with an <see cref="IDsDocumentCloseGuard"/> export.</summary>
	public interface IDsDocumentCloseGuardMetadata {
		/// <summary>Stable, ordinally unique guard name.</summary>
		string Name { get; }
		/// <summary>Numeric order in which the guard is evaluated.</summary>
		double Order { get; }
	}

	/// <summary>Exports a document close guard with deterministic ordering metadata.</summary>
	[MetadataAttribute, AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
	public sealed class ExportDsDocumentCloseGuardAttribute : ExportAttribute, IDsDocumentCloseGuardMetadata {
		/// <summary>Creates an export attribute.</summary>
		public ExportDsDocumentCloseGuardAttribute(string name, double order)
			: base(typeof(IDsDocumentCloseGuard)) {
			Name = name ?? throw new ArgumentNullException(nameof(name));
			Order = order;
		}

		/// <inheritdoc/>
		public string Name { get; }
		/// <inheritdoc/>
		public double Order { get; }
	}

	/// <summary>Well-known close guard ordering values.</summary>
	public static class DsDocumentCloseGuardConstants {
		/// <summary>Order used by the bundle workspace guard.</summary>
		public const double ORDER_BUNDLE_WORKSPACE = 1000d;
		/// <summary>Default order used by guards without a more specific position.</summary>
		public const double ORDER_DEFAULT = double.MaxValue;
	}

	/// <summary>Coordinates all document close guards and their authorized mutations.</summary>
	public interface IDsDocumentCloseGuardService {
		/// <summary>
		/// Evaluates every guard synchronously on the main dispatcher and invokes the authorized
		/// mutation only when all guards approve.
		/// </summary>
		bool TryExecute(IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason,
			Func<bool> authorizedAction);
	}

	/// <summary>Convenience overload for mutation callbacks that do not return a status.</summary>
	public static class DsDocumentCloseGuardServiceExtensions {
		/// <summary>Runs an action after all close guards approve.</summary>
		public static bool TryExecute(this IDsDocumentCloseGuardService service,
			IReadOnlyList<IDsDocument> documents, DsDocumentCloseReason reason, Action authorizedAction) {
			if (service is null)
				throw new ArgumentNullException(nameof(service));
			if (authorizedAction is null)
				throw new ArgumentNullException(nameof(authorizedAction));
			return service.TryExecute(documents, reason, () => {
				authorizedAction();
				return true;
			});
		}
	}
}

# Upstream provenance

The source files in `../` are vendored from the official .NET runtime source
at tag `v10.0.11`, commit
`79d0c463f1b55624c874a11585f7e47731e8d675`.

The normative dependency closure and all SHA-256 values are maintained in
`docs/specs/dotnet-single-file-bundles.md`, section 3.9. The five patch files
in this directory are generated with LF line endings using:

```text
diff -u --label a/<upstream-path> --label b/<upstream-path>
```

They are the only adaptations applied to the upstream source. The provenance
test verifies the pristine source hashes, complete patch hashes, ordered
per-hunk hashes, result hashes, compile-item closure, and license hash before
the project is built.

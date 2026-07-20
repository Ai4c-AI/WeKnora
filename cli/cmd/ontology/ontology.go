// Package ontologycmd implements the `ontology` command subtree for managing
// ontology review in a knowledge base.
package ontologycmd

import (
	"github.com/spf13/cobra"

	"github.com/Tencent/WeKnora/cli/internal/cmdutil"
)

const ontologyLong = `Manage ontology slice review and operations.

Ontology slices are chunk-scoped schema fragments (micro-TBoxes) extracted
by the LLM pipeline. Use 'ontology backfill' to generate review queue entries
for existing chunks that already have ontology data.`

// NewCmdOntology builds the parent `ontology` command. Called from cli/cmd/root.go.
func NewCmdOntology(f *cmdutil.Factory) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "ontology <subcommand>",
		Short: "Manage ontology slices and review",
		Long:  ontologyLong,
	}
	cmd.AddCommand(NewCmdBackfill(f))
	return cmd
}

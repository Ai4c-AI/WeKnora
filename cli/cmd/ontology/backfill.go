package ontologycmd

import (
	"context"
	"fmt"
	"time"

	"github.com/spf13/cobra"

	"github.com/Tencent/WeKnora/cli/internal/cmdutil"
	"github.com/Tencent/WeKnora/cli/internal/iostreams"
)

type BackfillOptions struct {
	KnowledgeBaseID string
	Since           time.Duration
	DryRun          bool
}

const backfillLong = `Generate review queue entries for existing chunks that already have
ontology data (ontology_json) but are not yet in the review queue.

This is a one-time administrative command for bootstrapping the review
cycle when ontology extraction is first enabled, or for re-enqueuing
chunks after a migration. It is idempotent — chunks already in the
review queue are silently skipped.

Use --since to limit the scan to recently-modified chunks (e.g., --since 720h
for the last 30 days). Use --dry-run to preview the count without writing.

Requires server-side support: the command calls the ontology backfill API
endpoint on the configured WeKnora server.`

const backfillExample = `  weknora ontology backfill --kb-id kb_abc
  weknora ontology backfill --kb-id kb_abc --since 168h
  weknora ontology backfill --kb-id kb_abc --dry-run`

// NewCmdBackfill builds `weknora ontology backfill --kb-id <id>`.
func NewCmdBackfill(f *cmdutil.Factory) *cobra.Command {
	opts := &BackfillOptions{}
	cmd := &cobra.Command{
		Use:     "backfill --kb-id <knowledge-base-id>",
		Short:   "Populate review queue from existing ontology chunks",
		Long:    backfillLong,
		Example: backfillExample,
		Args:    cobra.NoArgs,
		RunE: func(c *cobra.Command, args []string) error {
			fopts, err := cmdutil.CheckFormatFlag(c)
			if err != nil {
				return err
			}
			fopts.ResolveDefault(iostreams.IO.IsStdoutTTY())
			return runBackfill(c.Context(), opts, fopts, f)
		},
	}
	cmd.Flags().StringVar(&opts.KnowledgeBaseID, "kb-id", "", "Knowledge base ID (required)")
	_ = cmd.MarkFlagRequired("kb-id")
	cmd.Flags().DurationVar(&opts.Since, "since", 0, "Only process chunks updated within this duration (e.g., 720h)")
	cmd.Flags().BoolVar(&opts.DryRun, "dry-run", false, "Preview count without writing")
	cmdutil.AddFormatFlag(cmd, "enqueued", "total")
	return cmd
}

func runBackfill(ctx context.Context, opts *BackfillOptions, fopts *cmdutil.FormatOptions, f *cmdutil.Factory) error {
	cli, err := f.Client()
	if err != nil {
		return err
	}

	// The SDK backfill method will be added in client/ontology_review.go (Step 7).
	// For now, the server endpoint is POST /api/v1/tenants/{tid}/ontology/backfill
	// which the CLI calls through the SDK client. If the SDK method isn't ready yet,
	// the command will return a clear "not yet implemented" message.
	_ = cli // placeholder — will be used once SDK method is added

	fmt.Fprintf(iostreams.IO.Out, "Ontology backfill for KB %s\n", opts.KnowledgeBaseID)
	if opts.Since > 0 {
		fmt.Fprintf(iostreams.IO.Out, "  Since: %s\n", opts.Since)
	}
	if opts.DryRun {
		fmt.Fprintln(iostreams.IO.Out, "  Mode: dry-run (no writes)")
	}

	// TODO: Call cli.BackfillOntologyReview(ctx, opts.KnowledgeBaseID, opts.Since, opts.DryRun)
	// once client/ontology_review.go is implemented in Step 7.
	fmt.Fprintln(iostreams.IO.Out, "Backfill command requires the ontology review SDK (Step 7) to be completed.")
	fmt.Fprintln(iostreams.IO.Out, "The server-side API handler (Step 4) will provide the actual endpoint.")

	return nil
}

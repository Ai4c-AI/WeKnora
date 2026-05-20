package config

import "testing"

func TestApplyOntologyDefaults(t *testing.T) {
	cfg := &Config{}

	applyOntologyDefaults(cfg)

	if cfg.Ontology == nil {
		t.Fatal("Ontology config is nil")
	}
	if cfg.Ontology.Enabled {
		t.Fatalf("Enabled = true, want false")
	}
	if cfg.Ontology.ReasonerURL != "http://ontology-reasoner:8090" {
		t.Fatalf("ReasonerURL = %q, want %q", cfg.Ontology.ReasonerURL, "http://ontology-reasoner:8090")
	}
	if cfg.Ontology.DefaultProfile != "n3-extended" {
		t.Fatalf("DefaultProfile = %q, want %q", cfg.Ontology.DefaultProfile, "n3-extended")
	}
	if cfg.Ontology.ConfidenceThreshold != 0.3 {
		t.Fatalf("ConfidenceThreshold = %v, want 0.3", cfg.Ontology.ConfidenceThreshold)
	}
	if cfg.Ontology.ExtractMinEntities != 2 {
		t.Fatalf("ExtractMinEntities = %d, want 2", cfg.Ontology.ExtractMinEntities)
	}
}

func TestBackfillConversationDefaultsResolvesExtractMicroTBoxPrompt(t *testing.T) {
	cfg := &Config{
		Conversation: &ConversationConfig{
			ExtractMicroTBoxPromptID: "default_extract_micro_tbox",
		},
		PromptTemplates: &PromptTemplatesConfig{
			GraphExtraction: []PromptTemplate{
				{ID: "default_extract_micro_tbox", Content: "extract a micro tbox"},
			},
		},
	}

	backfillConversationDefaults(cfg)

	if cfg.Conversation.ExtractMicroTBoxPrompt != "extract a micro tbox" {
		t.Fatalf("ExtractMicroTBoxPrompt = %q, want %q", cfg.Conversation.ExtractMicroTBoxPrompt, "extract a micro tbox")
	}
}

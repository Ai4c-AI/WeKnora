package config

import (
	"strings"
	"testing"

	"github.com/spf13/viper"
)

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

func TestApplyOntologyDefaultsPreservesExplicitZeroNumericValues(t *testing.T) {
	cfg := &Config{
		Ontology: &OntologyConfig{
			ConfidenceThreshold: 0,
			ExtractMinEntities:  0,
		},
	}

	applyOntologyDefaultsWithExplicitness(cfg, true, true)

	if cfg.Ontology.ConfidenceThreshold != 0 {
		t.Fatalf("ConfidenceThreshold = %v, want explicit zero preserved", cfg.Ontology.ConfidenceThreshold)
	}
	if cfg.Ontology.ExtractMinEntities != 0 {
		t.Fatalf("ExtractMinEntities = %d, want explicit zero preserved", cfg.Ontology.ExtractMinEntities)
	}
}

func TestApplyOntologyDefaultsAppliesNumericDefaultsWhenUnset(t *testing.T) {
	cfg := &Config{
		Ontology: &OntologyConfig{
			ConfidenceThreshold: 0,
			ExtractMinEntities:  0,
		},
	}

	applyOntologyDefaultsWithExplicitness(cfg, false, false)

	if cfg.Ontology.ConfidenceThreshold != 0.3 {
		t.Fatalf("ConfidenceThreshold = %v, want 0.3", cfg.Ontology.ConfidenceThreshold)
	}
	if cfg.Ontology.ExtractMinEntities != 2 {
		t.Fatalf("ExtractMinEntities = %d, want 2", cfg.Ontology.ExtractMinEntities)
	}
}

func TestApplyOntologyDefaultsInvalidEnvWithOmittedNumericConfigKeepsDefaults(t *testing.T) {
	viper.Reset()
	t.Cleanup(viper.Reset)
	viper.AutomaticEnv()
	viper.SetEnvKeyReplacer(strings.NewReplacer(".", "_"))

	t.Setenv("ONTOLOGY_CONFIDENCE_THRESHOLD", "not-float")
	t.Setenv("ONTOLOGY_EXTRACT_MIN_ENTITIES", "not-int")

	cfg := &Config{
		Ontology: &OntologyConfig{
			ConfidenceThreshold: 0,
			ExtractMinEntities:  0,
		},
	}

	applyOntologyDefaults(cfg)

	if cfg.Ontology.ConfidenceThreshold != 0.3 {
		t.Fatalf("ConfidenceThreshold = %v, want default 0.3 when invalid env is ignored", cfg.Ontology.ConfidenceThreshold)
	}
	if cfg.Ontology.ExtractMinEntities != 2 {
		t.Fatalf("ExtractMinEntities = %d, want default 2 when invalid env is ignored", cfg.Ontology.ExtractMinEntities)
	}
}

func TestApplyOntologyDefaultsUsesEnvOverrides(t *testing.T) {
	t.Setenv("ONTOLOGY_ENABLE", "true")
	t.Setenv("ONTOLOGY_REASONER_URL", " https://reasoner.example.test ")
	t.Setenv("ONTOLOGY_DEFAULT_PROFILE", " n3-strict ")
	t.Setenv("ONTOLOGY_CONFIDENCE_THRESHOLD", "0.85")
	t.Setenv("ONTOLOGY_EXTRACT_MIN_ENTITIES", "5")

	cfg := &Config{
		Ontology: &OntologyConfig{
			Enabled:             false,
			ReasonerURL:         "http://config-reasoner:8090",
			DefaultProfile:      "config-profile",
			ConfidenceThreshold: 0.4,
			ExtractMinEntities:  3,
		},
	}

	applyOntologyDefaults(cfg)

	if !cfg.Ontology.Enabled {
		t.Fatal("Enabled = false, want true")
	}
	if cfg.Ontology.ReasonerURL != "https://reasoner.example.test" {
		t.Fatalf("ReasonerURL = %q, want %q", cfg.Ontology.ReasonerURL, "https://reasoner.example.test")
	}
	if cfg.Ontology.DefaultProfile != "n3-strict" {
		t.Fatalf("DefaultProfile = %q, want %q", cfg.Ontology.DefaultProfile, "n3-strict")
	}
	if cfg.Ontology.ConfidenceThreshold != 0.85 {
		t.Fatalf("ConfidenceThreshold = %v, want 0.85", cfg.Ontology.ConfidenceThreshold)
	}
	if cfg.Ontology.ExtractMinEntities != 5 {
		t.Fatalf("ExtractMinEntities = %d, want 5", cfg.Ontology.ExtractMinEntities)
	}
}

func TestApplyOntologyDefaultsIgnoresInvalidEnvOverrides(t *testing.T) {
	t.Setenv("ONTOLOGY_ENABLE", "not-bool")
	t.Setenv("ONTOLOGY_CONFIDENCE_THRESHOLD", "not-float")
	t.Setenv("ONTOLOGY_EXTRACT_MIN_ENTITIES", "not-int")

	cfg := &Config{
		Ontology: &OntologyConfig{
			Enabled:             true,
			ConfidenceThreshold: 0.7,
			ExtractMinEntities:  4,
		},
	}

	applyOntologyDefaults(cfg)

	if !cfg.Ontology.Enabled {
		t.Fatal("Enabled = false, want existing true when env is invalid")
	}
	if cfg.Ontology.ConfidenceThreshold != 0.7 {
		t.Fatalf("ConfidenceThreshold = %v, want existing 0.7 when env is invalid", cfg.Ontology.ConfidenceThreshold)
	}
	if cfg.Ontology.ExtractMinEntities != 4 {
		t.Fatalf("ExtractMinEntities = %d, want existing 4 when env is invalid", cfg.Ontology.ExtractMinEntities)
	}
}

func TestValidateConfigRejectsInvalidOntologyConfidenceThreshold(t *testing.T) {
	cfg := &Config{
		Ontology: &OntologyConfig{ConfidenceThreshold: 1.1},
	}

	err := ValidateConfig(cfg)
	if err == nil {
		t.Fatal("ValidateConfig returned nil, want error")
	}
	if !strings.Contains(err.Error(), "ontology.confidence_threshold must be between 0 and 1") {
		t.Fatalf("ValidateConfig error = %q, want ontology confidence threshold error", err.Error())
	}
}

func TestValidateConfigRejectsNegativeOntologyExtractMinEntities(t *testing.T) {
	cfg := &Config{
		Ontology: &OntologyConfig{ExtractMinEntities: -1},
	}

	err := ValidateConfig(cfg)
	if err == nil {
		t.Fatal("ValidateConfig returned nil, want error")
	}
	if !strings.Contains(err.Error(), "ontology.extract_min_entities must be >= 0") {
		t.Fatalf("ValidateConfig error = %q, want ontology extract min entities error", err.Error())
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

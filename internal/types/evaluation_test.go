package types

import "testing"

func TestEvaluationTaskStringDoesNotInitializeJieba(t *testing.T) {
	result := (&EvaluationTask{ID: "task-1"}).String()

	if result == "" {
		t.Fatal("expected JSON string")
	}
}

package types

type MicroTBox struct {
	Classes    []ClassDecl         `json:"classes"`
	Properties []PropertyDecl      `json:"properties"`
	Shapes     []ShapeDecl         `json:"shapes"`
	Aliases    map[string][]string `json:"aliases"`
	Axioms     []FreeAxiom         `json:"axioms"`
	Confidence float64             `json:"confidence"`
}

type ClassDecl struct {
	ID           string   `json:"id"`
	Label        string   `json:"label"`
	SubClassOf   *string  `json:"subClassOf"`
	DisjointWith []string `json:"disjointWith"`
	Evidence     string   `json:"evidence"`
}

type PropertyDecl struct {
	ID              string   `json:"id"`
	Label           string   `json:"label"`
	Domain          string   `json:"domain"`
	Range           string   `json:"range"`
	Characteristics []string `json:"characteristics"`
	InverseOf       *string  `json:"inverseOf"`
	Evidence        string   `json:"evidence"`
}

type ShapeDecl struct {
	TargetClass string            `json:"target_class"`
	Constraints []ShapeConstraint `json:"constraints"`
	Evidence    string            `json:"evidence"`
}

type ShapeConstraint struct {
	Property string   `json:"property"`
	MinCount *int     `json:"min_count"`
	MaxCount *int     `json:"max_count"`
	Datatype *string  `json:"datatype"`
	InValues []string `json:"in_values"`
}

type FreeAxiom struct {
	Statement string `json:"statement"`
	Evidence  string `json:"evidence"`
}

type Triple struct {
	Subject   string `json:"s"`
	Predicate string `json:"p"`
	Object    string `json:"o"`
}

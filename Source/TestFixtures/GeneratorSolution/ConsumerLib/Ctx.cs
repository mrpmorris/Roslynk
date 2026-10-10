namespace ConsumerLib;

// The hand-written half of the Ctx partial type. No member here may be named like one of the generator's
// (or mention a generated member by name): resolution tests rely on the generated declarations being absent
// from this project's regular documents, which is what Roslyn's declaration search pre-filters on.
public partial class Ctx
{
	public string Name => "ctx";
}

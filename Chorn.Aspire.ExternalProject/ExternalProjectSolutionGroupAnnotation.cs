namespace Chorn.Aspire.ExternalProject;

internal class ExternalProjectSolutionGroupAnnotation : IResourceAnnotation
{
	public ExternalProjectSolutionGroupAnnotation(string solutionGroup)
	{
		this.SolutionGroup = solutionGroup;
	}

	public string SolutionGroup { get; set; }
}
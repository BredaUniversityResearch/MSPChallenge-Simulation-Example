using System.Diagnostics.CodeAnalysis;

namespace MSPChallenge_Simulation_Example.Communication.DataModel;

[SuppressMessage("ReSharper", "InconsistentNaming")]
class KPI
{
	public string name;		//Name of the KPI
	public int month;		//Month this KPI applies to
	public double value;	//The value of this KPI
	public string type;		//kpiCategory: 'ECOLOGY','ENERGY','SHIPPING'
	public string unit;		//the unit of the KPI.
	public int country = -1;//Country id this KPI applies to. -1 implies there's no country associated.
}
